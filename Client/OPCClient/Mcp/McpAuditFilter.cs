using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OPCClient.Security;
using OPCClient.Storage;

namespace OPCClient.Mcp;

/// <summary>
/// Filter um jeden Tool-Aufruf: schreibt genau einen Audit-Eintrag (ok, denied oder error) und lehnt schreibende Tools
/// ohne Rolle writer ab, bevor sie laufen. Schreibend heißt: das Tool ist nicht als ReadOnly markiert (fail closed).
/// </summary>
public static class McpAuditFilter
{
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Create(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (context, cancellationToken) =>
        {
            var audit = context.Services!.GetRequiredService<AuditLog>();
            var user = context.User;
            var action = context.Params?.Name ?? "unbekannt";
            var arguments = FormatArguments(context.Params?.Arguments);

            if (RequiresWriter(context.MatchedPrimitive) && user?.IsInRole(Roles.Writer) != true)
            {
                var message = OpcTools.WriterRequiredMessage(action);
                audit.Record(AuditChannels.Mcp, user, action, arguments, AuditOutcomes.Denied, message);
                return ErrorResult(message);
            }

            try
            {
                var result = await next(context, cancellationToken);
                if (result.IsError == true)
                    audit.Record(AuditChannels.Mcp, user, action, arguments, AuditOutcomes.Error, TextOf(result));
                else
                    audit.Record(AuditChannels.Mcp, user, action, arguments, AuditOutcomes.Ok);
                return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                audit.Record(AuditChannels.Mcp, user, action, arguments, AuditOutcomes.Error, ex.Message);
                throw;
            }
        };

    private static bool RequiresWriter(IMcpServerPrimitive? primitive) =>
        primitive is McpServerTool tool && tool.ProtocolTool.Annotations?.ReadOnlyHint != true;

    private static string? FormatArguments(IDictionary<string, JsonElement>? arguments) =>
        arguments is { Count: > 0 } ? string.Join(" ", arguments.Select(a => $"{a.Key}={a.Value}")) : null;

    private static string TextOf(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    private static CallToolResult ErrorResult(string message) =>
        new() { IsError = true, Content = [new TextContentBlock { Text = message }] };
}
