using System.Threading.Channels;
using _1Rad.Application.Interfaces;

namespace _1Rad.Infrastructure.BackgroundJobs;

/// <summary>
/// Channel-backed implementation of <see cref="IDicomExtractionQueue"/>.
/// Single-consumer (the hosted service) is the expected usage pattern but
/// multiple consumers would also be safe.
/// </summary>
public class DicomExtractionQueue : IDicomExtractionQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    public void Enqueue(Guid assetId)
    {
        // Channel writes to an unbounded channel never block and never return false.
        _channel.Writer.TryWrite(assetId);
    }

    public IAsyncEnumerable<Guid> DequeueAllAsync(CancellationToken cancellationToken)
        => _channel.Reader.ReadAllAsync(cancellationToken);
}
