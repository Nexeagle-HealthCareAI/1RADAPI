using System.Threading.Channels;
using _1Rad.Application.Interfaces;

namespace _1Rad.Infrastructure.BackgroundJobs;

/// <summary>
/// Channel-backed implementation of <see cref="IDicomExtractionQueue"/>.
/// The worker runs MULTIPLE parallel consumers, so SingleReader is false —
/// System.Threading.Channels delivers each item to exactly one reader, so the
/// consumers compete safely without any item being processed twice or lost.
/// </summary>
public class DicomExtractionQueue : IDicomExtractionQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(
        new UnboundedChannelOptions
        {
            SingleReader = false, // multiple parallel consumers in DicomExtractionWorker
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
