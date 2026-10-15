using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OPCClient.Opc;
using OPCClient.Storage;

namespace OPCClient.Tests;

/// <summary>Persistentes Audit-Log (MCP und REST), GET /api/audit und das Rate Limit für /mcp.</summary>
public sealed class AuditAndRateLimitTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private SqliteAuditStore Audit => factory.Services.GetRequiredService<SqliteAuditStore>();
    private SqliteMessageStore Messages => factory.Services.GetRequiredService<SqliteMessageStore>();

    private IReadOnlyList<AuditEntry> Entries(string action, string channel) =>
        Audit.Query(new AuditFilter(Action: action, Limit: 500)).Items.Where(e => e.Channel == channel).ToList();

    private long CreateFailedMessage(string name)
    {
        var id = Messages.Push(new OpcMessage("Audit-Test", name, 1, DateTime.UtcNow));
        Assert.Equal(id, Messages.TryPop("Audit-Test")!.Id);
        Messages.Fail(id, attempts: 3, error: "kaputt", maxAttempts: 3);
        return id;
    }

    // ---------- MCP ----------

    [Fact]
    public async Task Successful_tool_call_is_audited_with_client_role_channel_and_arguments()
    {
        Messages.Push(new OpcMessage("Presse-1", "Audit-Ok", 1, DateTime.UtcNow));
        await using var client = await McpTestSupport.ConnectAsync(factory, ApiFactory.ReaderKey);

        await client.CallToolAsync("get_latest_values", new Dictionary<string, object?> { ["machine"] = "Presse-1" });

        var entry = Entries("get_latest_values", AuditChannels.Mcp).Single(e => e.Arguments == "machine=Presse-1");
        Assert.Equal("test-reader", entry.Client);
        Assert.Equal("reader", entry.Role);
        Assert.Equal(AuditOutcomes.Ok, entry.Outcome);
        Assert.Null(entry.Error);
        Assert.True(entry.Timestamp > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task Failed_tool_call_is_audited_as_error_with_the_message()
    {
        await using var client = await McpTestSupport.ConnectAsync(factory, ApiFactory.ReaderKey);

        var result = await client.CallToolAsync("query_messages", new Dictionary<string, object?> { ["state"] = "Audit-Kaputt" });

        Assert.True(result.IsError);
        var entry = Entries("query_messages", AuditChannels.Mcp).Single(e => e.Arguments == "state=Audit-Kaputt");
        Assert.Equal(AuditOutcomes.Error, entry.Outcome);
        Assert.Contains("Unbekannter Zustand", entry.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reader_attempting_retry_is_denied_and_audited_without_touching_the_message()
    {
        var id = CreateFailedMessage("Audit-Denied");
        await using var client = await McpTestSupport.ConnectAsync(factory, ApiFactory.ReaderKey);

        var result = await client.CallToolAsync("retry_message", new Dictionary<string, object?> { ["id"] = id });

        Assert.True(result.IsError);
        Assert.Contains("writer", McpTestSupport.TextOf(result), StringComparison.Ordinal);
        Assert.Equal(MessageState.Failed, Messages.Get(id)!.State);
        var entry = Entries("retry_message", AuditChannels.Mcp).Single(e => e.Arguments == $"id={id}");
        Assert.Equal(AuditOutcomes.Denied, entry.Outcome);
        Assert.Equal("reader", entry.Role);
    }

    [Fact]
    public async Task Writer_retry_is_audited_as_ok()
    {
        var id = CreateFailedMessage("Audit-Writer");
        await using var client = await McpTestSupport.ConnectAsync(factory, ApiFactory.WriterKey);

        await client.CallToolAsync("retry_message", new Dictionary<string, object?> { ["id"] = id });

        var entry = Entries("retry_message", AuditChannels.Mcp).Single(e => e.Arguments == $"id={id}");
        Assert.Equal(AuditOutcomes.Ok, entry.Outcome);
        Assert.Equal("test-writer", entry.Client);
        Assert.Equal("writer", entry.Role);
    }

    [Fact]
    public async Task Arguments_in_the_audit_log_are_shortened_to_300_characters()
    {
        await using var client = await McpTestSupport.ConnectAsync(factory, ApiFactory.ReaderKey);

        await client.CallToolAsync("query_alarms", new Dictionary<string, object?> { ["name"] = new string('x', 1000) });

        var entry = Entries("query_alarms", AuditChannels.Mcp).First(e => e.Arguments!.Contains("xxxxxxxxxx", StringComparison.Ordinal));
        Assert.True(entry.Arguments!.Length <= 303, entry.Arguments.Length.ToString());
        Assert.EndsWith("...", entry.Arguments, StringComparison.Ordinal);
    }

    // ---------- REST ----------

    [Fact]
    public async Task Rest_retry_by_a_reader_returns_403_and_is_audited_as_denied()
    {
        var id = CreateFailedMessage("Audit-Rest-Denied");

        var response = await factory.CreateReaderClient().PostAsync(new Uri($"/api/messages/{id}/retry", UriKind.Relative), null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var entry = Entries("retry_message", AuditChannels.Rest).Single(e => e.Arguments == $"id={id}");
        Assert.Equal(AuditOutcomes.Denied, entry.Outcome);
        Assert.Equal("test-reader", entry.Client);
    }

    [Fact]
    public async Task Rest_retry_by_a_writer_is_audited_as_ok_and_unknown_id_as_error()
    {
        var id = CreateFailedMessage("Audit-Rest-Ok");
        var client = factory.CreateWriterClient();

        var accepted = await client.PostAsync(new Uri($"/api/messages/{id}/retry", UriKind.Relative), null);
        var missing = await client.PostAsync(new Uri("/api/messages/987654/retry", UriKind.Relative), null);

        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(AuditOutcomes.Ok, Entries("retry_message", AuditChannels.Rest).Single(e => e.Arguments == $"id={id}").Outcome);
        var error = Entries("retry_message", AuditChannels.Rest).Single(e => e.Arguments == "id=987654");
        Assert.Equal(AuditOutcomes.Error, error.Outcome);
        Assert.Contains("404", error.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rest_reads_are_not_audited_and_unauthenticated_calls_leave_no_entry()
    {
        var before = Audit.Query(new AuditFilter(Action: "retry_message")).Total;

        await factory.CreateClient().PostAsync(new Uri("/api/messages/1/retry", UriKind.Relative), null);
        await factory.CreateReaderClient().GetAsync(new Uri("/api/machines", UriKind.Relative));

        Assert.Equal(before, Audit.Query(new AuditFilter(Action: "retry_message")).Total);
        Assert.Empty(Audit.Query(new AuditFilter(Action: "GET")).Items);
    }

    // ---------- GET /api/audit ----------

    [Fact]
    public async Task Audit_endpoint_is_for_writers_only()
    {
        var anonymous = await factory.CreateClient().GetAsync(new Uri("/api/audit", UriKind.Relative));
        var reader = await factory.CreateReaderClient().GetAsync(new Uri("/api/audit", UriKind.Relative));
        var writer = await factory.CreateWriterClient().GetAsync(new Uri("/api/audit", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, reader.StatusCode);
        Assert.Equal(HttpStatusCode.OK, writer.StatusCode);
    }

    [Fact]
    public async Task Audit_endpoint_filters_and_returns_the_newest_first()
    {
        Audit.Add(new AuditEntry(DateTime.UtcNow.AddMinutes(-2), "filter-client", "reader", AuditChannels.Mcp, "filter_action", "a=1", AuditOutcomes.Ok));
        Audit.Add(new AuditEntry(DateTime.UtcNow.AddMinutes(-1), "filter-client", "reader", AuditChannels.Mcp, "filter_action", "a=2", AuditOutcomes.Error, "boom"));
        Audit.Add(new AuditEntry(DateTime.UtcNow, "other-client", "writer", AuditChannels.Rest, "filter_action", "a=3", AuditOutcomes.Denied));

        var response = await factory.CreateWriterClient().GetAsync(
            new Uri("/api/audit?client=filter-client&action=filter_action&limit=10", UriKind.Relative));

        var page = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, page.GetProperty("total").GetInt32());
        Assert.Equal("a=2", page.GetProperty("items")[0].GetProperty("arguments").GetString());
        Assert.Equal("error", page.GetProperty("items")[0].GetProperty("outcome").GetString());

        var denied = await (await factory.CreateWriterClient().GetAsync(
            new Uri("/api/audit?action=filter_action&outcome=denied", UriKind.Relative))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, denied.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Audit_endpoint_rejects_an_unknown_outcome()
    {
        var response = await factory.CreateWriterClient().GetAsync(new Uri("/api/audit?outcome=kaputt", UriKind.Relative));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Audit_is_not_available_as_mcp_tool()
    {
        await using var client = await McpTestSupport.ConnectAsync(factory, ApiFactory.WriterKey);

        var tools = await client.ListToolsAsync();

        Assert.DoesNotContain(tools, t => t.Name.Contains("audit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Purge_removes_only_old_audit_entries()
    {
        using var db = new TempDatabase();
        var store = new SqliteAuditStore(db.Path);
        store.Add(new AuditEntry(DateTime.UtcNow.AddDays(-100), "c", "reader", AuditChannels.Mcp, "alt", null, AuditOutcomes.Ok));
        store.Add(new AuditEntry(DateTime.UtcNow, "c", "reader", AuditChannels.Mcp, "neu", null, AuditOutcomes.Ok));

        Assert.Equal(1, store.Purge(TimeSpan.FromDays(90)));
        Assert.Equal("neu", Assert.Single(store.Query(new AuditFilter()).Items).Action);
    }

    // ---------- Rate Limit ----------

    private static HttpRequestMessage McpRequest() =>
        new(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", Encoding.UTF8, "application/json"),
        };

    [Fact]
    public async Task Mcp_requests_over_the_limit_get_429_with_retry_after_while_rest_stays_available()
    {
        await using var limited = factory.WithWebHostBuilder(b => b.UseSetting("RateLimit:McpRequestsPerMinute", "3"));
        var client = McpTestSupport.CreateClientWithKey(limited, ApiFactory.ReaderKey);

        for (var i = 0; i < 3; i++)
            Assert.NotEqual(HttpStatusCode.TooManyRequests, (await client.SendAsync(McpRequest())).StatusCode);
        var rejected = await client.SendAsync(McpRequest());

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.True(rejected.Headers.RetryAfter?.Delta?.TotalSeconds is > 0 and <= 60, "Retry-After fehlt oder unplausibel");
        Assert.Contains("Zu viele Anfragen", await rejected.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(new Uri("/api/machines", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task Mcp_limit_is_counted_per_client()
    {
        await using var limited = factory.WithWebHostBuilder(b => b.UseSetting("RateLimit:McpRequestsPerMinute", "1"));
        var reader = McpTestSupport.CreateClientWithKey(limited, ApiFactory.ReaderKey);
        var writer = McpTestSupport.CreateClientWithKey(limited, ApiFactory.WriterKey);

        await reader.SendAsync(McpRequest());
        var readerSecond = await reader.SendAsync(McpRequest());
        var writerFirst = await writer.SendAsync(McpRequest());

        Assert.Equal(HttpStatusCode.TooManyRequests, readerSecond.StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, writerFirst.StatusCode);
    }
}
