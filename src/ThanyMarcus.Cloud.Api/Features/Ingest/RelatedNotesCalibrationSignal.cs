using System.Threading.Channels;

namespace ThanyMarcus.Cloud.Api.Features.Ingest;

// Lazy, query-driven trigger for the per-user Auto recalibration. The related endpoint pokes this on
// every request (cheap, non-blocking); the worker coalesces and gates on staleness + a cooldown.
public sealed class RelatedNotesCalibrationSignal
{
    private readonly Channel<bool> channel = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });

    public void Trigger() => channel.Writer.TryWrite(true);

    public ChannelReader<bool> Reader => channel.Reader;
}
