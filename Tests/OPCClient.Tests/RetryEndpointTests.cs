using System.Net;
using Microsoft.Extensions.DependencyInjection;
using OPCClient.Opc;
using OPCClient.Storage;

namespace OPCClient.Tests;

/// <summary>
/// POST /api/messages/{id}/retry. Die Nachrichten gehören zu einer nicht registrierten Maschine,
/// damit kein Worker sie nebenbei verarbeitet und der Zustand deterministisch bleibt.
/// </summary>
public sealed class RetryEndpointTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static Uri RetryUrl(long id) => new($"/api/messages/{id}/retry", UriKind.Relative);

    private SqliteMessageStore Store => factory.Services.GetRequiredService<SqliteMessageStore>();

    private long PushFailed()
    {
        var id = Store.Push(new OpcMessage("Retry-Test", "T", 1, DateTime.UtcNow));
        var popped = Store.TryPop("Retry-Test")!;
        Assert.Equal(id, popped.Id);
        Store.Fail(id, 3, "kaputt", 3);
        Assert.Equal(MessageState.Failed, Store.Get(id)!.State);
        return id;
    }

    [Fact]
    public async Task Writer_requeues_a_failed_message()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var id = PushFailed();

        var response = await factory.CreateWriterClient().PostAsync(RetryUrl(id), null, cts.Token);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var stored = Store.Get(id)!;
        Assert.Equal(MessageState.Pending, stored.State);
        Assert.Equal(0, stored.Attempts);
        Assert.Null(stored.Error);
    }

    [Fact]
    public async Task Writer_gets_404_for_a_message_that_has_not_failed()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var id = Store.Push(new OpcMessage("Retry-Test", "T", 2, DateTime.UtcNow));

        var response = await factory.CreateWriterClient().PostAsync(RetryUrl(id), null, cts.Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(MessageState.Pending, Store.Get(id)!.State);
    }

    [Fact]
    public async Task Reader_gets_403_and_the_failed_message_stays_failed()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var id = PushFailed();

        var response = await factory.CreateReaderClient().PostAsync(RetryUrl(id), null, cts.Token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(MessageState.Failed, Store.Get(id)!.State);
    }
}
