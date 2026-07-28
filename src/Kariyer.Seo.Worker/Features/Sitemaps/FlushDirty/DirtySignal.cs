using System.Threading.Channels;

namespace Kariyer.Seo.Worker.Features.Sitemaps.FlushDirty;

/// <summary>
/// The coalescing signal between the freshness consumers and the incremental flush
/// (PLAN §7).
///
/// A single-slot channel, and the capacity of one is the entire design. A batch of a hundred
/// expiries lands in seconds; without coalescing, each would trigger its own re-projection of
/// <c>sitemap-jobs</c> — a hundred full streams of a several-hundred-thousand-URL file, all
/// but the last immediately obsolete. <c>DropWrite</c> means the ninety-ninth signal is
/// discarded because a flush is already owed, and one flush covers every one of them.
///
/// The signal is an OPTIMISATION, not the source of truth. It lives in memory and dies with
/// the process; what actually records that a flush is owed is the <c>dirty</c> column, which
/// is why a pod killed between a consumer's commit and its flush recovers at boot rather than
/// waiting a full cron interval.
/// </summary>
public sealed class DirtySignal
{
    private readonly Channel<byte> _channel = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });

    /// <summary>Records that a flush is owed. Never blocks, never throws, never awaits —
    /// it is called from inside a consumer's transaction path.</summary>
    public void Raise() => _channel.Writer.TryWrite(0);

    /// <summary>Waits for the next signal.</summary>
    public ValueTask<byte> WaitAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAsync(cancellationToken);

    /// <summary>
    /// Discards signals accumulated so far.
    ///
    /// Must be called at the END of the debounce window and BEFORE the flush begins — that
    /// is the point at which every signal raised so far is genuinely about to be covered.
    ///
    /// Draining after the flush instead would be a silent correctness bug: a change
    /// committed WHILE the projection was streaming may have arrived after its row was read,
    /// so it is not in the file that just went live. Its signal has to survive, so the next
    /// iteration flushes again. The cost of getting this backwards is one job sitting in the
    /// wrong state until the next full rebuild — invisible, and up to 45 minutes long.
    /// </summary>
    public void Drain()
    {
        while (_channel.Reader.TryRead(out _))
        {
            // Coalesced.
        }
    }
}
