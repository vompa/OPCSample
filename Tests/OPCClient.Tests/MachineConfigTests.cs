using Microsoft.Extensions.Configuration;
using OPCClient.Opc;

namespace OPCClient.Tests;

public class MachineConfigTests
{
    private static List<MachineConfig> Bind(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build()
            .GetSection("Opc:Machines").Get<List<MachineConfig>>() ?? [];

    [Fact]
    public void Security_is_off_by_default_for_existing_configurations()
    {
        var machines = Bind(new()
        {
            ["Opc:Machines:0:Name"] = "Presse-1",
            ["Opc:Machines:0:Endpoint"] = "opc.tcp://localhost:4840",
        });

        Assert.False(machines.Single().UseSecurity);
    }

    [Fact]
    public void UseSecurity_is_bound_per_machine()
    {
        var machines = Bind(new()
        {
            ["Opc:Machines:0:Name"] = "Presse-1",
            ["Opc:Machines:0:UseSecurity"] = "true",
            ["Opc:Machines:1:Name"] = "Presse-2",
        });

        Assert.True(machines[0].UseSecurity);
        Assert.False(machines[1].UseSecurity);
    }

    [Fact]
    public void Shipped_appsettings_do_not_auto_accept_untrusted_certificates()
    {
        // Schutz gegen versehentliches Zurückdrehen des sicheren Standards
        var config = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: false)
            .Build();

        Assert.False(config.GetValue<bool>("Opc:AutoAcceptUntrustedCertificates"));
    }
}
