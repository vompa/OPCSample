namespace OPCClient.Security;

public static class Roles
{
    /// <summary>Darf lesen (Maschinen, Nachrichten, aktuelle Werte).</summary>
    public const string Reader = "reader";

    /// <summary>Darf zusätzlich Zustände ändern (z. B. fehlgeschlagene Nachrichten erneut einplanen).</summary>
    public const string Writer = "writer";
}

public static class Policies
{
    public const string Writer = "Writer";
}

public sealed class ApiKeyOptions
{
    public const string SectionName = "Authentication";

    public IList<ApiKeyEntry> ApiKeys { get; } = [];
}

public sealed class ApiKeyEntry
{
    /// <summary>Anzeigename des Clients, taucht im Audit-Log auf.</summary>
    public string Name { get; set; } = "";

    /// <summary>SHA-256 des Keys als Hex-String (erzeugbar mit: dotnet run -- hash-key KEY).</summary>
    public string KeySha256 { get; set; } = "";

    /// <summary>"reader" (Standard) oder "writer".</summary>
    public string Role { get; set; } = Roles.Reader;
}
