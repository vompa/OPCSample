using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OPCSimulator;

// appsettings.json liegt neben der Anwendung, unabhängig vom Arbeitsverzeichnis
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

builder.Configuration.AddCommandLine(args, new Dictionary<string, string>
{
    ["--port"] = $"{SimulatorOptions.SectionName}:Port",
    ["--interval"] = $"{SimulatorOptions.SectionName}:UpdateIntervalMs",
    ["--seed"] = $"{SimulatorOptions.SectionName}:Seed",
    ["--overheat"] = $"{SimulatorOptions.SectionName}:OverheatProbability"
});

builder.Services.Configure<SimulatorOptions>(builder.Configuration.GetSection(SimulatorOptions.SectionName));
builder.Services.AddHostedService<SimulatorService>();

await builder.Build().RunAsync();

/// <summary>Betreibt den Simulator, solange der Host läuft (Strg+C beendet sauber).</summary>
internal sealed class SimulatorService(IOptions<SimulatorOptions> options, ILogger<SimulatorService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var simulator = await SimulatorHost.StartAsync(options.Value, stoppingToken, logger);
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Strg+C: der Simulator wird beim Verlassen des using-Blocks beendet
        }
    }
}
