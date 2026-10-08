using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using Serilog;

namespace OPCClient.Opc;

public static class OpcSessionFactory
{
    public static readonly ITelemetryContext Telemetry = DefaultTelemetry.Create(_ => { });

    public static async Task<Session> CreateAsync(
        string endpointUrl, string applicationName = "OPCClient", CancellationToken ct = default,
        bool useSecurity = false, bool autoAcceptUntrustedCertificates = false)
    {
        if (!useSecurity)
            Log.ForContext("Machine", applicationName).Warning(
                "OPC-Verbindung ohne Signierung und Verschlüsselung (UseSecurity = false). Nur in vertrauenswürdigen Netzen verwenden.");

        var config = new ApplicationConfiguration
        {
            ApplicationName = applicationName,
            ApplicationType = ApplicationType.Client,
            ApplicationUri = $"urn:localhost:{applicationName}",
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = "Directory",
                    StorePath = "pki/own",
                    SubjectName = $"CN={applicationName}"
                },
                TrustedPeerCertificates = new CertificateTrustList { StoreType = "Directory", StorePath = "pki/trusted" },
                TrustedIssuerCertificates = new CertificateTrustList { StoreType = "Directory", StorePath = "pki/issuer" },
                RejectedCertificateStore = new CertificateTrustList { StoreType = "Directory", StorePath = "pki/rejected" },
                // Standard: false. Der Zertifikat des Servers muss dann in pki/trusted liegen (abgelehnte landen in pki/rejected).
                AutoAcceptUntrustedCertificates = autoAcceptUntrustedCertificates
            },
            TransportConfigurations = new TransportConfigurationCollection(),
            TransportQuotas = new TransportQuotas { OperationTimeout = 15000 },
            ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 60000 }
        };

        await config.ValidateAsync(ApplicationType.Client, ct);

        // Abgelehnte Server-Zertifikate nachvollziehbar loggen, statt nur mit einem Verbindungsfehler zu scheitern
        config.CertificateValidator.CertificateValidation += (_, e) =>
        {
            if (e.Accept) return;
            Log.ForContext("Machine", applicationName).Warning(
                "OPC-Serverzertifikat abgelehnt ({Subject}, {Thumbprint}): {Reason}. Nach Prüfung in pki/trusted/certs ablegen.",
                e.Certificate.Subject, e.Certificate.Thumbprint, e.Error.StatusCode);
        };

        var app = new ApplicationInstance(Telemetry) { ApplicationName = applicationName, ApplicationConfiguration = config };
        await app.CheckApplicationInstanceCertificatesAsync(false, null, ct);

        var endpoint = await CoreClientUtils.SelectEndpointAsync(config, endpointUrl, useSecurity, Telemetry, ct);
        var configuredEndpoint = new ConfiguredEndpoint(null, endpoint, EndpointConfiguration.Create(config));

        var session = await new DefaultSessionFactory(Telemetry).CreateAsync(
            config, configuredEndpoint, false, applicationName, 60000, new UserIdentity(), null, ct);
        return (Session)session;
    }
}
