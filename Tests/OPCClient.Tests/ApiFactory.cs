using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using OPCClient.Security;

namespace OPCClient.Tests;

/// <summary>
/// Startet den echten Dienst im Speicher: eigene SQLite-Datei, zwei Test-Keys und unerreichbare OPC-Endpoints
/// (Port 1), damit die konfigurierten Maschinen "Presse-1" und "Drehbank-2" deterministisch nicht verbunden sind.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public const string ReaderKey = "test-reader-key-0123456789";
    public const string WriterKey = "test-writer-key-0123456789";
    private const string UnreachableEndpoint = "opc.tcp://127.0.0.1:1";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"opc-api-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Database:Path", _dbPath);
        builder.UseSetting("Opc:Machines:0:Endpoint", UnreachableEndpoint);
        builder.UseSetting("Opc:Machines:1:Endpoint", UnreachableEndpoint);
        builder.UseSetting("Authentication:ApiKeys:0:Name", "test-reader");
        builder.UseSetting("Authentication:ApiKeys:0:KeySha256", ApiKeyHasher.Hash(ReaderKey));
        builder.UseSetting("Authentication:ApiKeys:0:Role", Roles.Reader);
        builder.UseSetting("Authentication:ApiKeys:1:Name", "test-writer");
        builder.UseSetting("Authentication:ApiKeys:1:KeySha256", ApiKeyHasher.Hash(WriterKey));
        builder.UseSetting("Authentication:ApiKeys:1:Role", Roles.Writer);
    }

    public HttpClient CreateClientWithKey(string key)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyDefaults.HeaderName, key);
        return client;
    }

    public HttpClient CreateReaderClient() => CreateClientWithKey(ReaderKey);

    public HttpClient CreateWriterClient() => CreateClientWithKey(WriterKey);

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            SqliteConnection.ClearAllPools();
            foreach (var file in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
                File.Delete(file);
        }
    }
}
