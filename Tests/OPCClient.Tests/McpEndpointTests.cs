using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OPCClient.Opc;
using OPCClient.Storage;

namespace OPCClient.Tests;

public sealed class McpEndpointTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private SqliteMessageStore Store => factory.Services.GetRequiredService<SqliteMessageStore>();

    private Task<McpClient> ConnectAsync(string key) => McpTestSupport.ConnectAsync(factory, key);

    private static string TextOf(CallToolResult result) => McpTestSupport.TextOf(result);

    [Fact]
    public async Task Mcp_endpoint_requires_authentication()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");

        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Agent_discovers_seven_tools_and_none_writes_to_the_machine()
    {
        await using var client = await ConnectAsync(ApiFactory.ReaderKey);

        var tools = await client.ListToolsAsync();

        Assert.Equal(
            ["get_active_alarms", "get_latest_values", "get_statistics", "list_machines", "query_alarms", "query_messages", "retry_message"],
            tools.Select(t => t.Name).Order());
        Assert.False(tools.Single(t => t.Name == "retry_message").ProtocolTool.Annotations?.ReadOnlyHint);
        foreach (var tool in tools.Where(t => t.Name != "retry_message"))
        {
            Assert.True(tool.ProtocolTool.Annotations?.ReadOnlyHint, tool.Name);
            Assert.True(tool.ProtocolTool.Annotations?.IdempotentHint, tool.Name);
        }
    }

    [Fact]
    public async Task List_machines_reports_both_disconnected_machines()
    {
        await using var client = await ConnectAsync(ApiFactory.ReaderKey);

        var result = await client.CallToolAsync("list_machines");

        Assert.NotEqual(true, result.IsError);
        var text = TextOf(result);
        Assert.Contains("Presse-1", text, StringComparison.Ordinal);
        Assert.Contains("Drehbank-2", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"connected\":true", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Latest_values_return_the_newest_value_per_variable()
    {
        Store.Push(new OpcMessage("Presse-1", "Mcp-Druck", 1.0, DateTime.UtcNow));
        Store.Push(new OpcMessage("Presse-1", "Mcp-Druck", 7.5, DateTime.UtcNow));
        await using var client = await ConnectAsync(ApiFactory.ReaderKey);

        var result = await client.CallToolAsync("get_latest_values", new Dictionary<string, object?> { ["machine"] = "Presse-1" });

        var text = TextOf(result);
        Assert.Contains("Mcp-Druck", text, StringComparison.Ordinal);
        Assert.Contains("7.5", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"value\":1}", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_machine_returns_an_error_the_agent_can_act_on()
    {
        await using var client = await ConnectAsync(ApiFactory.ReaderKey);

        var result = await client.CallToolAsync("get_latest_values", new Dictionary<string, object?> { ["machine"] = "Gibt-es-nicht" });

        Assert.True(result.IsError);
        Assert.Contains("list_machines", TextOf(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Query_messages_filters_and_caps_the_page_size()
    {
        for (var i = 0; i < 3; i++)
            Store.Push(new OpcMessage("Presse-1", "Mcp-Filter", i, DateTime.UtcNow));
        await using var client = await ConnectAsync(ApiFactory.ReaderKey);

        var result = await client.CallToolAsync("query_messages", new Dictionary<string, object?>
        {
            ["name"] = "Mcp-Filter",
            ["limit"] = 100_000,
        });

        var text = TextOf(result);
        Assert.Contains("\"total\":3", text, StringComparison.Ordinal);
        Assert.Contains("\"limit\":100", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Query_messages_rejects_an_unknown_state_with_the_valid_values()
    {
        await using var client = await ConnectAsync(ApiFactory.ReaderKey);

        var result = await client.CallToolAsync("query_messages", new Dictionary<string, object?> { ["state"] = "Kaputt" });

        Assert.True(result.IsError);
        Assert.Contains("Failed", TextOf(result), StringComparison.Ordinal);
    }

    private long CreateFailedMessage(string name)
    {
        var id = Store.Push(new OpcMessage("Presse-1", name, 1, DateTime.UtcNow));
        Assert.Equal(id, Store.TryPop("Presse-1")!.Id);
        Store.Fail(id, attempts: 3, error: "Ziel nicht erreichbar", maxAttempts: 3);
        return id;
    }

    [Fact]
    public async Task Reader_key_cannot_requeue_messages()
    {
        var id = CreateFailedMessage("Mcp-Reader");
        await using var client = await ConnectAsync(ApiFactory.ReaderKey);

        var result = await client.CallToolAsync("retry_message", new Dictionary<string, object?> { ["id"] = id });

        Assert.True(result.IsError);
        Assert.Contains("writer", TextOf(result), StringComparison.Ordinal);
        Assert.Equal(MessageState.Failed, Store.Get(id)!.State);
    }

    [Fact]
    public async Task Writer_key_can_requeue_a_failed_message()
    {
        var id = CreateFailedMessage("Mcp-Writer");
        await using var client = await ConnectAsync(ApiFactory.WriterKey);

        var result = await client.CallToolAsync("retry_message", new Dictionary<string, object?> { ["id"] = id });

        Assert.NotEqual(true, result.IsError);
        Assert.NotEqual(MessageState.Failed, Store.Get(id)!.State);
    }

    [Fact]
    public async Task Retrying_a_message_that_is_not_failed_is_rejected()
    {
        var id = Store.Push(new OpcMessage("Presse-1", "Mcp-Pending", 1, DateTime.UtcNow));
        await using var client = await ConnectAsync(ApiFactory.WriterKey);

        var result = await client.CallToolAsync("retry_message", new Dictionary<string, object?> { ["id"] = id });

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task Wrong_key_is_rejected_when_connecting() =>
        await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync("wrong-key"));
}
