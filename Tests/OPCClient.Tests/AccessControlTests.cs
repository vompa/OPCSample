using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OPCClient.Opc;
using OPCClient.Storage;

namespace OPCClient.Tests;

public sealed class AccessControlTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static Uri Url(string path) => new(path, UriKind.Relative);

    [Theory]
    [InlineData("/api/machines")]
    [InlineData("/api/messages")]
    [InlineData("/api/messages/1")]
    [InlineData("/api/machines/Presse-1/latest")]
    public async Task Api_requires_authentication(string path)
    {
        var response = await factory.CreateClient().GetAsync(Url(path));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Bearer", response.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Wrong_key_is_rejected() =>
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await factory.CreateClientWithKey("wrong").GetAsync(Url("/api/machines"))).StatusCode);

    [Fact]
    public async Task Bearer_token_works_like_api_key_header()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiFactory.ReaderKey);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Url("/api/machines"))).StatusCode);
    }

    [Fact]
    public async Task Reader_cannot_requeue_messages()
    {
        var response = await factory.CreateReaderClient().PostAsync(Url("/api/messages/1/retry"), null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Writer_passes_authorization_and_gets_a_business_answer()
    {
        // Es gibt keine Nachricht 424242: 404 beweist, dass die Rolle ausreichte und die Fachlogik lief.
        var response = await factory.CreateWriterClient().PostAsync(Url("/api/messages/424242/retry"), null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Health_is_public_and_reports_degraded_while_a_machine_is_disconnected()
    {
        var response = await factory.CreateClient().GetAsync(Url("/api/health"));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("degraded", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Health_does_not_leak_machine_details()
    {
        var text = await factory.CreateClient().GetStringAsync(Url("/api/health"));

        Assert.DoesNotContain("Presse-1", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Machines_endpoint_shows_connection_state_and_counts()
    {
        var store = factory.Services.GetRequiredService<SqliteMessageStore>();
        store.Push(new OpcMessage("Presse-1", "Temperatur", 21.5, DateTime.UtcNow));

        var machines = await factory.CreateReaderClient()
            .GetFromJsonAsync<JsonElement>(Url("/api/machines"));

        var press = machines.EnumerateArray().Single(m => m.GetProperty("name").GetString() == "Presse-1");
        Assert.False(press.GetProperty("connected").GetBoolean());
        Assert.True(press.GetProperty("messages").GetProperty("Pending").GetInt32() >= 1);
    }

    [Fact]
    public async Task Swagger_is_not_exposed_outside_development()
    {
        var response = await factory.CreateClient().GetAsync(Url("/swagger/v1/swagger.json"));

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }
}
