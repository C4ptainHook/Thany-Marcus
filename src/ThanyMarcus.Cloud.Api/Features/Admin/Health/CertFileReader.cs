namespace ThanyMarcus.Cloud.Api.Features.Admin.Health;

public sealed partial class CertFileReader(
    IConfiguration config,
    ILogger<CertFileReader> log)
{
    private readonly string liveDir = config["Cert:LiveDir"]
        ?? throw new InvalidOperationException("Cert:LiveDir required");

    public bool IsCertReady()
    {
        var fullchain = Path.Combine(liveDir, "fullchain.pem");
        var privkey   = Path.Combine(liveDir, "privkey.pem");
        var ready = File.Exists(fullchain) && File.Exists(privkey);
        if (!ready) LogNotPresent(log, liveDir);
        return ready;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
        Message = "Cert not present at {LiveDir}")]
    private static partial void LogNotPresent(ILogger logger, string liveDir);
}
