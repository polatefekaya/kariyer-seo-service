using System.Text;
using Kariyer.Seo.Domain.Ports;

namespace Kariyer.Seo.IntegrationTests.Sitemaps;

/// <summary>
/// An in-memory <see cref="ISitemapSink"/> that enforces the SAME atomicity contract as the
/// R2 one, and fails loudly if a caller breaks it.
///
/// A fake rather than MinIO, deliberately. What needs testing here is not "can we speak S3" —
/// the AWS SDK can — but "does a reader mid-rebuild ever see a partial index". That is a
/// property of the ORDERING the caller uses, and a fake can assert it directly by refusing to
/// publish an index that names a child which is not yet live. Against MinIO the same bug
/// would produce a passing test with a race in it.
/// </summary>
public sealed class FakeSitemapSink : ISitemapSink
{
    private readonly Dictionary<string, StoredFile> _live = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>Every file a crawler could currently fetch.</summary>
    public IReadOnlyDictionary<string, StoredFile> Live
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, StoredFile>(_live, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>How many staged sets have been promoted.</summary>
    public int Commits { get; private set; }

    /// <summary>Files actually written, ignoring the ones the checksum short-circuit skipped.</summary>
    public List<string> Uploaded { get; } = [];

    public Task<ISitemapStage> BeginAsync(CancellationToken cancellationToken) =>
        Task.FromResult<ISitemapStage>(new Stage(this));

    public Task<IReadOnlyDictionary<string, string>> GetLiveChecksumsAsync(
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyDictionary<string, string>>(
                _live.ToDictionary(e => e.Key, e => e.Value.Checksum, StringComparer.Ordinal));
        }
    }

    /// <summary>
    /// Reads the live set the way a crawler would: fetch the index, then fetch every child it
    /// names. Throws if the index names something that is not there, which is exactly the
    /// torn state PLAN §6.3 exists to make impossible.
    /// </summary>
    public IReadOnlyList<string> FollowIndex(string indexFileName)
    {
        lock (_gate)
        {
            if (!_live.TryGetValue(indexFileName, out StoredFile? index))
            {
                return [];
            }

            List<string> children = [];

            foreach (string line in index.Text.Split('\n'))
            {
                int start = line.IndexOf("<loc>", StringComparison.Ordinal);

                if (start < 0)
                {
                    continue;
                }

                int end = line.IndexOf("</loc>", StringComparison.Ordinal);
                string loc = line[(start + 5)..end];
                string fileName = loc[(loc.LastIndexOf('/') + 1)..];

                if (!_live.ContainsKey(fileName))
                {
                    throw new InvalidOperationException(
                        $"The live index names '{fileName}', which is not published. A crawler "
                        + "reading right now would receive a 404 — this is the torn set the "
                        + "stage-and-swap ordering exists to prevent.");
                }

                children.Add(fileName);
            }

            return children;
        }
    }

    private sealed class Stage(FakeSitemapSink sink) : ISitemapStage
    {
        private readonly Dictionary<string, MemoryStream> _staged = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _checksums = new(StringComparer.Ordinal);
        private readonly HashSet<string> _unchanged = new(StringComparer.Ordinal);

        public Stream OpenWrite(string fileName, string contentType)
        {
            MemoryStream stream = new();
            _staged[fileName] = stream;
            return new NonClosingStream(stream);
        }

        public void RecordChecksum(string fileName, string checksum) =>
            _checksums[fileName] = checksum;

        public void MarkUnchanged(string fileName) => _unchanged.Add(fileName);

        public Task CommitAsync(
            string indexFileName,
            IReadOnlyCollection<string> obsolete,
            CancellationToken cancellationToken)
        {
            lock (sink._gate)
            {
                // Children first, index last — the same ordering the R2 sink uses, so a test
                // that observes the live set mid-commit sees what a crawler would.
                foreach ((string fileName, MemoryStream body) in _staged)
                {
                    if (fileName == indexFileName || _unchanged.Contains(fileName))
                    {
                        continue;
                    }

                    Publish(fileName, body);
                }

                if (_staged.TryGetValue(indexFileName, out MemoryStream? index)
                    && !_unchanged.Contains(indexFileName))
                {
                    Publish(indexFileName, index);
                }

                foreach (string fileName in obsolete)
                {
                    sink._live.Remove(fileName);
                }

                sink.Commits++;
            }

            return Task.CompletedTask;
        }

        private void Publish(string fileName, MemoryStream body)
        {
            sink._live[fileName] = new StoredFile(
                Encoding.UTF8.GetString(body.ToArray()),
                _checksums.GetValueOrDefault(fileName, string.Empty));

            sink.Uploaded.Add(fileName);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NonClosingStream(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            inner.Write(buffer, offset, count);

        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing) => base.Dispose(disposing);
    }
}

/// <summary>One published file.</summary>
public sealed record StoredFile(string Text, string Checksum);
