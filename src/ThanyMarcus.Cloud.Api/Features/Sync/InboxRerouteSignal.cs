using System.Threading.Channels;

namespace ThanyMarcus.Cloud.Api.Features.Sync;

public sealed class InboxRerouteSignal
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
