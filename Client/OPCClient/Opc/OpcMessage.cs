namespace OPCClient.Opc;

/// <param name="Id">Id in der Datenbank (0 = noch nicht gespeichert). Dient als stabiler Idempotenz-Schlüssel.</param>
public record OpcMessage(string Machine, string Name, object? Value, DateTime Timestamp, long Id = 0);
