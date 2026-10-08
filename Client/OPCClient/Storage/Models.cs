using System.Text.Json;

namespace OPCClient.Storage;

public enum MessageState { Pending = 0, Processing = 1, Done = 2, Failed = 3 }

public record MessageDto(long Id, string Machine, string Name, JsonElement? Value,
    DateTime Timestamp, MessageState State, int Attempts, string? Error);

public record MessageFilter(string? Machine = null, string? Name = null, MessageState? State = null,
    DateTime? From = null, DateTime? To = null, int Limit = 100, int Offset = 0);

public record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Limit, int Offset);

public record MachineDto(string Name, bool Connected, Dictionary<string, int> Messages);
