using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi;
using OPCClient.Mcp;
using OPCClient.Security;
using OPCClient.Api;
using OPCClient.Opc;
using OPCClient.Storage;
using Serilog;
using Serilog.Events;

// Hilfsbefehl: dotnet run -- hash-key "<dein-key>"  (gibt den SHA-256 für die Konfiguration aus)
if (args is ["hash-key", var plainKey])
{
    Console.WriteLine(ApiKeyHasher.Hash(plainKey));
    return;
}

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] [{Machine}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File("logs/opc-.log", rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3} [{Machine}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog();
    builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? "http://localhost:5080");

    var databasePath = builder.Configuration["Database:Path"] ?? "opc.db";
    builder.Services.AddSingleton(new SqliteMessageStore(databasePath));
    builder.Services.AddSingleton(new SqliteAlarmStore(databasePath));
    builder.Services.AddSingleton(new SqliteAuditStore(databasePath));
    builder.Services.AddSingleton(new SqliteStatisticsReader(databasePath));
    builder.Services.AddSingleton<AlarmMonitor>();
    builder.Services.AddSingleton<AuditLog>();
    builder.Services.AddSingleton<MachineRegistry>();
    builder.Services.AddHostedService<OpcHostedService>();

    // --- Ausgang der Nachrichten: Log (Standard) oder Webhook ---
    builder.Services.AddOptions<SinkOptions>()
        .Bind(builder.Configuration.GetSection(SinkOptions.SectionName))
        .Validate(o => o.IsValid(out _), "Ungültige Konfiguration unter Opc:Sink (Typ Log/Webhook, Webhook nur über HTTPS).")
        .ValidateOnStart();
    builder.Services.AddHttpClient(WebhookMessageHandler.ClientName);
    if (builder.Configuration[$"{SinkOptions.SectionName}:Type"] == SinkOptions.Webhook)
        builder.Services.AddSingleton<IMessageHandler, WebhookMessageHandler>();
    else
        builder.Services.AddSingleton<IMessageHandler, LoggingMessageHandler>();

    // --- MCP: Zugriff für KI-Agenten (stateless, gleiche Authentifizierung wie die REST-API) ---
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddMcpServer()
        .WithHttpTransport(options => options.Stateless = true)
        .WithTools<OPCClient.Mcp.OpcTools>()
        .WithRequestFilters(filters => filters.AddCallToolFilter(McpAuditFilter.Create));

    // --- Aufräumen erledigter Nachrichten, beendeter Alarme und alter Audit-Einträge ---
    builder.Services.AddOptions<RetentionOptions>().Bind(builder.Configuration.GetSection(RetentionOptions.SectionName));
    builder.Services.AddHostedService<RetentionService>();

    // --- Authentifizierung: API-Key mit Rollen. Ohne konfigurierten Key startet der Dienst nicht (fail closed). ---
    builder.Services.AddOptions<ApiKeyOptions>()
        .Bind(builder.Configuration.GetSection(ApiKeyOptions.SectionName))
        .Validate(o => o.ApiKeys.Count > 0,
            "Es ist kein API-Key konfiguriert (Authentication:ApiKeys). Der Dienst startet nicht ungeschützt.")
        .Validate(o => o.ApiKeys.All(k => k.Name.Length > 0 && ApiKeyHasher.TryParseHash(k.KeySha256, out _)
                                          && k.Role is Roles.Reader or Roles.Writer),
            "Jeder API-Key braucht Name, SHA-256-Hash (64 Hex-Zeichen) und Rolle 'reader' oder 'writer'.")
        .ValidateOnStart();

    builder.Services.AddAuthentication(ApiKeyDefaults.Scheme)
        .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyDefaults.Scheme, _ => { });
    builder.Services.AddAuthorizationBuilder()
        .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
        .AddPolicy(Policies.Writer, p => p.RequireRole(Roles.Writer));

    // --- Rate Limiting pro Client (bzw. pro IP, solange niemand angemeldet ist; bremst Key-Raten) ---
    // Global für alle Anfragen, strenger für /mcp (Policy "mcp"): ein Agent soll das System nicht fluten.
    var requestsPerMinute = builder.Configuration.GetValue("RateLimit:RequestsPerMinute", 120);
    var mcpRequestsPerMinute = builder.Configuration.GetValue("RateLimit:McpRequestsPerMinute", 30);
    builder.Services.AddRateLimiter(limiter =>
    {
        limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            PerClientPerMinute(context, requestsPerMinute));
        limiter.AddPolicy(RateLimitPolicies.Mcp, context => PerClientPerMinute(context, mcpRequestsPerMinute));
        limiter.OnRejected = RateLimitRejection.WriteAsync;
    });

    // Enums als Text (Pending/Done/...) statt Zahl
    builder.Services.ConfigureHttpJsonOptions(o =>
        o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(o =>
    {
        o.SwaggerDoc("v1", new() { Title = "OPC Gateway API", Version = "v1" });
        o.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Name = ApiKeyDefaults.HeaderName,
            Description = "API-Key (Development: dev-reader-key bzw. dev-writer-key)"
        });
        o.AddSecurityRequirement(document => new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference("ApiKey", document)] = []
        });
    });

    var app = builder.Build();

    // Swagger nur in Development: das Schema muss nicht öffentlich sein
    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    app.UseAuthentication();
    app.UseRateLimiter();   // nach Authentication (kennt den Client), vor Authorization (bremst auch fehlgeschlagene Versuche)
    app.UseMiddleware<RestAuditMiddleware>();   // vor Authorization, damit auch abgewiesene (403) Schreibversuche im Audit landen
    app.UseAuthorization();
    app.MapOpcApi();
    app.MapMcp("/mcp").RequireRateLimiting(RateLimitPolicies.Mcp);
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Anwendung unerwartet beendet");
}
finally
{
    await Log.CloseAndFlushAsync();
}

static RateLimitPartition<string> PerClientPerMinute(HttpContext context, int permitLimit) =>
    RateLimitPartition.GetFixedWindowLimiter(
        context.User.Identity?.Name ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = Math.Max(permitLimit, 1), Window = TimeSpan.FromMinutes(1) });

public partial class Program;
