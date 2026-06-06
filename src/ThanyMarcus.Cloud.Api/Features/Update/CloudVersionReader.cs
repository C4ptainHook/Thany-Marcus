using Microsoft.Extensions.Options;
using ThanyMarcus.Shared.CloudAdmin;

namespace ThanyMarcus.Cloud.Api.Features.Update;

public sealed partial class CloudVersionReader(
    IOptions<CloudUpdateOptions> options,
    ILogger<CloudVersionReader> logger)
{
    public const string CurrentVersionFile = "current_version";

    public string CurrentVersion()
    {
        var path = Path.Combine(options.Value.StateDir, CurrentVersionFile);
        if (!File.Exists(path))
            return CloudAdminHealthResponse.CurrentApiVersion;

        try
        {
            var value = File.ReadAllText(path).Trim();
            return string.IsNullOrEmpty(value) ? CloudAdminHealthResponse.CurrentApiVersion : value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogReadFailed(logger, path, ex);
            return CloudAdminHealthResponse.CurrentApiVersion;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to read cloud version state at {Path}")]
    private static partial void LogReadFailed(ILogger logger, string path, Exception ex);
}
