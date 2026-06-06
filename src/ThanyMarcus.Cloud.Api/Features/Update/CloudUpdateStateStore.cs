using System.Text.Json;
using Microsoft.Extensions.Options;
using NodaTime;
using ThanyMarcus.Shared.CloudUpdate;

namespace ThanyMarcus.Cloud.Api.Features.Update;

public sealed partial class CloudUpdateStateStore(
    IOptions<CloudUpdateOptions> options,
    CloudVersionReader version,
    IClock clock,
    ILogger<CloudUpdateStateStore> logger)
{
    public const string PendingFile = "pending-update.json";
    public const string StatusFile = "update-status.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public CloudUpdateStatus ReadStatus()
    {
        var path = Path.Combine(options.Value.StateDir, StatusFile);
        if (File.Exists(path))
        {
            try
            {
                var status = JsonSerializer.Deserialize<CloudUpdateStatus>(File.ReadAllText(path), Json);
                if (status is not null) return status;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                LogStatusReadFailed(logger, path, ex);
            }
        }
        return new CloudUpdateStatus(CloudUpdatePhases.Idle, version.CurrentVersion(), null, null, null);
    }

    public bool IsBusy()
    {
        var phase = ReadStatus().Phase;
        return phase is CloudUpdatePhases.Queued or CloudUpdatePhases.Verifying
            or CloudUpdatePhases.Pulling or CloudUpdatePhases.Applying or CloudUpdatePhases.HealthCheck;
    }

    public async Task QueueAsync(ApplyUpdateRequest bundle, CancellationToken ct)
    {
        Directory.CreateDirectory(options.Value.StateDir);

        var status = new CloudUpdateStatus(
            CloudUpdatePhases.Queued, version.CurrentVersion(), bundle.Version, "Update queued", clock.GetCurrentInstant().ToString());
        await WriteAtomicAsync(Path.Combine(options.Value.StateDir, StatusFile), JsonSerializer.Serialize(status, Json), ct);
        await WriteAtomicAsync(Path.Combine(options.Value.StateDir, PendingFile), JsonSerializer.Serialize(bundle, Json), ct);
    }

    private static async Task WriteAtomicAsync(string path, string contents, CancellationToken ct)
    {
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, contents, ct);
        File.Move(tmp, path, overwrite: true);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to read update status at {Path}")]
    private static partial void LogStatusReadFailed(ILogger logger, string path, Exception ex);
}
