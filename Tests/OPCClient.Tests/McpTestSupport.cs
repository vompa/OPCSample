using Microsoft.AspNetCore.Mvc.Testing;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OPCClient.Security;

namespace OPCClient.Tests;

/// <summary>Gemeinsame Hilfen für Tests, die den echten /mcp-Endpunkt mit einem MCP-Client ansprechen.</summary>
internal static class McpTestSupport
{
    public static async Task<McpClient> ConnectAsync<TProgram>(WebApplicationFactory<TProgram> factory, string key)
        where TProgram : class
    {
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri("http://localhost/mcp"),
                AdditionalHeaders = new Dictionary<string, string> { ["X-Api-Key"] = key },
            },
            factory.CreateClient(),
            ownsHttpClient: true);
        return await McpClient.CreateAsync(transport);
    }

    /// <summary>HTTP-Client mit Key, auch für abgeleitete Factories (<c>WithWebHostBuilder</c>) mit anderer Konfiguration.</summary>
    public static HttpClient CreateClientWithKey<TProgram>(WebApplicationFactory<TProgram> factory, string key)
        where TProgram : class
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyDefaults.HeaderName, key);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");
        return client;
    }

    public static string TextOf(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
}
