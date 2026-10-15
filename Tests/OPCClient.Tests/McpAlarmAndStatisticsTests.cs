using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using OPCClient.Opc;
using OPCClient.Storage;

namespace OPCClient.Tests;

/// <summary>Alarm- und Statistik-Tools über den echten /mcp-Endpunkt sowie die passenden REST-Endpunkte.</summary>
public sealed class McpAlarmAndStatisticsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly DateTime Start = DateTime.UtcNow.AddMinutes(-30);

    private SqliteMessageStore Messages => factory.Services.GetRequiredService<SqliteMessageStore>();
    private SqliteAlarmStore Alarms => factory.Services.GetRequiredService<SqliteAlarmStore>();

    private Task<McpClient> ConnectAsync() => McpTestSupport.ConnectAsync(factory, ApiFactory.ReaderKey);

    private static async Task<string> CallAsync(McpClient client, string tool, Dictionary<string, object?>? args = null,
        bool expectError = false)
    {
        var result = await client.CallToolAsync(tool, args);
        Assert.Equal(expectError, result.IsError == true);
        return McpTestSupport.TextOf(result);
    }

    private void SeedTemperature(string machine, string name, params double[] values)
    {
        for (var i = 0; i < values.Length; i++)
            Messages.Push(new OpcMessage(machine, name, values[i], Start.AddMinutes(i)));
    }

    [Fact]
    public async Task Active_alarms_lists_raised_and_hides_cleared_alarms()
    {
        Alarms.Raise("Presse-1", "AlarmTool-Aktiv", AlarmKind.High, 80, 91.5, Start);
        Alarms.Raise("Presse-1", "AlarmTool-Beendet", AlarmKind.High, 80, 91.5, Start);
        Alarms.Clear("Presse-1", "AlarmTool-Beendet", AlarmKind.High, 60, Start.AddMinutes(1));
        await using var client = await ConnectAsync();

        var text = await CallAsync(client, "get_active_alarms", new() { ["machine"] = "Presse-1" });

        Assert.Contains("AlarmTool-Aktiv", text, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"High\"", text, StringComparison.Ordinal);
        Assert.Contains("91.5", text, StringComparison.Ordinal);
        Assert.DoesNotContain("AlarmTool-Beendet", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Active_alarms_for_an_unknown_machine_points_to_list_machines()
    {
        await using var client = await ConnectAsync();

        var text = await CallAsync(client, "get_active_alarms", new() { ["machine"] = "Gibt-es-nicht" }, expectError: true);

        Assert.Contains("list_machines", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Query_alarms_pages_filters_and_caps_the_page_size()
    {
        for (var i = 0; i < 3; i++)
        {
            Alarms.Raise("Presse-1", "AlarmQuery", AlarmKind.High, 80, 90 + i, Start.AddMinutes(i));
            Alarms.Clear("Presse-1", "AlarmQuery", AlarmKind.High, 50, Start.AddMinutes(i + 1));
        }
        Alarms.Raise("Presse-1", "AlarmQuery", AlarmKind.Low, 10, 5, Start.AddMinutes(10));
        await using var client = await ConnectAsync();

        var all = await CallAsync(client, "query_alarms", new() { ["name"] = "AlarmQuery", ["limit"] = 100_000 });
        var active = await CallAsync(client, "query_alarms", new() { ["name"] = "AlarmQuery", ["activeOnly"] = true });
        var page = await CallAsync(client, "query_alarms", new() { ["name"] = "AlarmQuery", ["limit"] = 2, ["offset"] = 2 });

        Assert.Contains("\"total\":4", all, StringComparison.Ordinal);
        Assert.Contains("\"limit\":100", all, StringComparison.Ordinal);
        Assert.Contains("\"total\":1", active, StringComparison.Ordinal);
        Assert.Contains("\"Low\"", active, StringComparison.Ordinal);
        Assert.Equal(2, JsonDocument.Parse(page).RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Statistics_returns_summary_and_buckets()
    {
        SeedTemperature("Presse-1", "StatTool", 10, 20, 30, 40);
        await using var client = await ConnectAsync();

        var text = await CallAsync(client, "get_statistics", new()
        {
            ["machine"] = "Presse-1",
            ["name"] = "StatTool",
            ["from"] = Start,
            ["to"] = Start.AddMinutes(10),
            ["bucketMinutes"] = 2,
        });

        var root = JsonDocument.Parse(text).RootElement;
        Assert.Equal(4, root.GetProperty("count").GetInt32());
        Assert.Equal(10, root.GetProperty("min").GetDouble());
        Assert.Equal(40, root.GetProperty("max").GetDouble());
        Assert.Equal(25, root.GetProperty("avg").GetDouble());
        Assert.Equal(10, root.GetProperty("first").GetProperty("value").GetDouble());
        Assert.Equal(40, root.GetProperty("last").GetProperty("value").GetDouble());
        Assert.Equal(2, root.GetProperty("buckets").GetArrayLength());
        Assert.Contains("nicht zeitgewichtet", root.GetProperty("note").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Statistics_of_a_text_variable_explains_that_it_is_not_numeric()
    {
        Messages.Push(new OpcMessage("Presse-1", "StatText", "Laufend", Start));
        await using var client = await ConnectAsync();

        var text = await CallAsync(client, "get_statistics", new() { ["machine"] = "Presse-1", ["name"] = "StatText" }, expectError: true);

        Assert.Contains("nicht numerisch", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Statistics_rejects_too_large_period_and_too_many_buckets()
    {
        SeedTemperature("Presse-1", "StatRange", 1);
        await using var client = await ConnectAsync();

        var period = await CallAsync(client, "get_statistics", new()
        {
            ["machine"] = "Presse-1", ["name"] = "StatRange", ["from"] = Start.AddDays(-40), ["to"] = Start,
        }, expectError: true);
        var buckets = await CallAsync(client, "get_statistics", new()
        {
            ["machine"] = "Presse-1", ["name"] = "StatRange", ["from"] = Start.AddDays(-2), ["to"] = Start, ["bucketMinutes"] = 5,
        }, expectError: true);

        Assert.Contains("31 Tage", period, StringComparison.Ordinal);
        Assert.Contains("200", buckets, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Statistics_for_unknown_machine_and_variable_give_actionable_errors()
    {
        SeedTemperature("Presse-1", "StatKnown", 1);
        await using var client = await ConnectAsync();

        var machine = await CallAsync(client, "get_statistics", new() { ["machine"] = "Gibt-es-nicht", ["name"] = "x" }, expectError: true);
        var variable = await CallAsync(client, "get_statistics", new() { ["machine"] = "Presse-1", ["name"] = "Gibt-es-nicht" }, expectError: true);

        Assert.Contains("list_machines", machine, StringComparison.Ordinal);
        Assert.Contains("get_latest_values", variable, StringComparison.Ordinal);
    }

    // ---------- REST ----------

    [Fact]
    public async Task Rest_alarms_endpoint_filters_and_requires_authentication()
    {
        Alarms.Raise("Drehbank-2", "AlarmRest", AlarmKind.High, 1600, 1700, Start);

        var anonymous = await factory.CreateClient().GetAsync(new Uri("/api/alarms", UriKind.Relative));
        var response = await factory.CreateReaderClient().GetAsync(new Uri("/api/alarms?machine=Drehbank-2&name=AlarmRest&activeOnly=true", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, page.GetProperty("total").GetInt32());
        Assert.Equal("High", page.GetProperty("items")[0].GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Rest_statistics_endpoint_returns_data_and_maps_errors_to_status_codes()
    {
        SeedTemperature("Drehbank-2", "StatRest", 5, 15);
        var client = factory.CreateReaderClient();
        var from = Uri.EscapeDataString(Start.AddMinutes(-1).ToString("o"));
        var to = Uri.EscapeDataString(Start.AddMinutes(10).ToString("o"));

        var ok = await client.GetAsync(new Uri($"/api/machines/Drehbank-2/statistics?name=StatRest&from={from}&to={to}", UriKind.Relative));
        var tooLarge = await client.GetAsync(new Uri($"/api/machines/Drehbank-2/statistics?name=StatRest&from={from}&to={Uri.EscapeDataString(Start.AddDays(60).ToString("o"))}", UriKind.Relative));
        var unknown = await client.GetAsync(new Uri("/api/machines/Drehbank-2/statistics?name=Gibt-es-nicht", UriKind.Relative));
        var missingName = await client.GetAsync(new Uri("/api/machines/Drehbank-2/statistics", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(10, (await ok.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("avg").GetDouble());
        Assert.Equal(HttpStatusCode.BadRequest, tooLarge.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, missingName.StatusCode);
    }
}
