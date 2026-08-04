using System.IO.Compression;
using Amazon.S3;
using Amazon.S3.Model;
using Kariyer.Seo.Domain.Ports;
using Kariyer.Seo.Domain.Sitemaps;
using Kariyer.Seo.Worker.Common.Configuration;
using Kariyer.Seo.Worker.Common.Telemetry;
using Microsoft.Extensions.Options;

namespace Kariyer.Seo.Worker.Common.Storage;

/// <summary>
/// The R2 implementation of <see cref="ISitemapSink"/> — stage, then swap (PLAN §6.3).
///
/// <b>How the atomicity works.</b> R2 has no multi-object transaction, so atomicity is built
/// from ordering instead. A rebuild writes every file under a staging prefix a crawler cannot
/// reach, then promotes them: children first, the index LAST, obsolete files deleted only
/// after the index has stopped pointing at them. At every instant in that sequence the live
/// index references only objects that exist:
///
/// <list type="bullet">
///   <item>before promotion — the old index, all old children present;</item>
///   <item>mid-promotion — still the old index; new children exist alongside but nothing
///   references them, so no crawler can find them;</item>
///   <item>after the index copy — the new index, and every child it names was already
///   promoted;</item>
///   <item>after cleanup — only files the new index names remain.</item>
/// </list>
///
/// A crawler fetching at any point gets a complete, self-consistent set. A process that dies
/// at any point leaves the live set untouched, and the next cron tick redoes the work.
///
/// <b>Why copy rather than write twice.</b> The promotion is a server-side
/// <c>CopyObject</c>: no bytes leave the pod, so the swap is fast and its duration does not
/// grow with the corpus. Writing directly to the live keys and hoping to finish quickly is
/// the alternative this design exists to reject.
/// </summary>
public sealed class SitemapSink(
    IAmazonS3 s3,
    IOptions<SeoOptions> options,
    ILogger<SitemapSink> logger) : ISitemapSink
{
    private R2Options R2 => options.Value.R2;

    public Task<ISitemapStage> BeginAsync(CancellationToken cancellationToken) =>
        Task.FromResult<ISitemapStage>(new Stage(s3, options.Value, logger));

    /// <summary>
    /// Checksums of the live files, read from object metadata.
    ///
    /// Metadata rather than the object body: this runs on every cron tick, and downloading
    /// a set of sitemaps just to hash them would make the short-circuit cost more than the
    /// upload it avoids.
    ///
    /// This is a SECONDARY source. The primary is <c>seo_rebuild_log</c>, which is
    /// transactional and cannot drift; this exists so that a bucket restored from backup, or
    /// written by an older version, is still comparable. A missing or unreadable value is
    /// simply absent from the result, which makes the caller upload — the safe direction.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> GetLiveChecksumsAsync(
        CancellationToken cancellationToken)
    {
        Dictionary<string, string> checksums = new(StringComparer.Ordinal);

        try
        {
            ListObjectsV2Request request = new()
            {
                BucketName = R2.Bucket,
                Prefix = R2.Prefix,
                MaxKeys = 1000,
            };

            ListObjectsV2Response response;

            do
            {
                response = await s3.ListObjectsV2Async(request, cancellationToken);

                // Defensive despite the non-nullable declaration: some S3 implementations
                // omit the element entirely for an empty listing, and the SDK surfaces that
                // as null. An empty bucket is the NORMAL state on a first deploy, so a
                // NullReferenceException here would break exactly the run that has the most
                // work to do.
                foreach (S3Object item in response.S3Objects ?? [])
                {
                    string fileName = ToFileName(item.Key);

                    if (!SitemapNames.IsOwned(fileName))
                    {
                        continue;
                    }

                    GetObjectMetadataResponse metadata = await s3.GetObjectMetadataAsync(
                        R2.Bucket, item.Key, cancellationToken);

                    string? checksum = metadata.Metadata[ChecksumMetadataKey];

                    if (!string.IsNullOrEmpty(checksum))
                    {
                        checksums[fileName] = checksum;
                    }
                }

                request.ContinuationToken = response.NextContinuationToken;
            }
            while (response.IsTruncated);
        }
        catch (Exception ex)
        {
            // Deliberately broad, and this is one of the few places that is right.
            //
            // The value this method returns is an OPTIMISATION HINT — it exists only so an
            // unchanged file can skip its upload. Every way of failing to obtain it, from an
            // AmazonS3Exception to a DNS failure to a timeout, has the same correct response:
            // assume nothing is current and upload everything. Catching only
            // AmazonS3Exception looked precise but let a NameResolutionFailure abort the
            // whole rebuild before a single file had been built — turning a missed
            // optimisation into the outage it was supposed to protect against.
            //
            // A genuinely unreachable bucket still fails, loudly, at the first PutObject.
            logger.LogWarning(ex,
                "Could not read live sitemap checksums from R2; every file will be re-uploaded "
                + "this run.");
        }

        return checksums;
    }

    internal const string ChecksumMetadataKey = "seo-checksum";

    private string ToFileName(string key)
    {
        string trimmed = key.StartsWith(R2.Prefix, StringComparison.Ordinal)
            ? key[R2.Prefix.Length..]
            : key;

        // Compression is an encoding, not part of the file's identity: the same document is
        // `sitemap-jobs-1.xml` whether or not it was stored gzipped, and the rebuild log
        // keys on the logical name.
        return trimmed.EndsWith(".gz", StringComparison.Ordinal) ? trimmed[..^3] : trimmed;
    }

    private sealed class Stage(IAmazonS3 s3, SeoOptions options, ILogger logger) : ISitemapStage
    {
        private readonly List<PendingUpload> _pending = [];
        private readonly List<Stream> _open = [];
        private bool _committed;

        private R2Options R2 => options.R2;

        public Stream OpenWrite(string fileName, string contentType)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

            PendingUpload upload = new(fileName, contentType);
            _pending.Add(upload);

            // A temp file, not a MemoryStream.
            //
            // The AWS SDK needs a seekable stream to compute the request signature, and a
            // sitemap chunk is up to 50 MiB uncompressed. Buffering that in the managed heap
            // — several chunks at once, on a pod sized for a background worker — is a large
            // object heap allocation per chunk and an OOM waiting for the day the catalogue
            // grows. Disk is the right place for a staging buffer, and it is deleted on
            // dispose whether the commit succeeded or not.
            FileStream file = new(
                upload.TempPath,
                new FileStreamOptions
                {
                    Mode = FileMode.Create,
                    Access = FileAccess.ReadWrite,
                    Share = FileShare.None,
                    Options = FileOptions.SequentialScan,
                });

            _open.Add(file);

            if (!R2.Compress)
            {
                return file;
            }

            // SmallestSize rather than Optimal. These files are written once every 45 minutes
            // and fetched by crawlers indefinitely, so CPU at write time is the cheapest
            // resource in the exchange.
            GZipStream gzip = new(file, CompressionLevel.SmallestSize, leaveOpen: true);
            _open.Add(gzip);

            return gzip;
        }

        public async Task CommitAsync(
            string indexFileName,
            IReadOnlyCollection<string> obsolete,
            CancellationToken cancellationToken)
        {
            // Close what the SINK owns, before uploading.
            //
            // The caller owns the stream OpenWrite returned and has already disposed it —
            // that is the contract on ISitemapSink.OpenWrite, and SitemapWriter honours it.
            // What is left here is the temp FileStream underneath, which the GZipStream was
            // deliberately given `leaveOpen: true` so as not to close, and which PutAsync
            // cannot File.OpenRead while this handle still holds it FileShare.None.
            //
            // DisposeAsync only — never a FlushAsync first. Dispose is idempotent and flushes
            // on the way out, which is what writes the gzip trailer; a gzip stream that has
            // not been disposed would upload as a truncated archive that every decompressor
            // rejects — a corrupt sitemap served with a 200. FlushAsync, by contrast, throws
            // ObjectDisposedException on an already-disposed stream, and since every caller
            // disposes, that is every stream: it discarded every staged set the service built
            // until it was removed. Reversed, so a gzip is always closed before the file
            // beneath it.
            //
            // Unlike DisposeAsync below, an IOException here is NOT swallowed. A temp file
            // that failed to flush to disk is truncated, and abandoning the stage is better
            // than publishing it.
            foreach (Stream stream in Enumerable.Reverse(_open))
            {
                await stream.DisposeAsync();
            }

            _open.Clear();

            // Files the caller verified are already live and byte-identical never touch the
            // network at all — neither the staging PUT nor the promoting COPY. On a quiet
            // catalogue that is every file, which turns the steady-state cost of a cron tick
            // from "re-upload the whole set" into "read some checksums".
            List<PendingUpload> changed = [.. _pending.Where(u => !u.Unchanged)];

            // ── 1. Everything into staging ──────────────────────────────────────
            foreach (PendingUpload upload in changed)
            {
                await PutAsync(upload, StagingKey(upload.FileName), cancellationToken);
            }

            // ── 2. Children live, index last ────────────────────────────────────
            //
            // This ordering IS the atomicity. Promoting the index first would publish a
            // document naming children that do not exist yet, and a crawler arriving in that
            // window records 404s against every one of them.
            foreach (PendingUpload upload in changed.Where(u => u.FileName != indexFileName))
            {
                await PromoteAsync(upload.FileName, cancellationToken);
            }

            PendingUpload? index = changed.FirstOrDefault(u => u.FileName == indexFileName);

            if (index is not null)
            {
                await PromoteAsync(index.FileName, cancellationToken);
            }

            // ── 3. Obsolete files, only now ─────────────────────────────────────
            //
            // After the index stopped naming them, never before: deleting a child the live
            // index still points at is the same 404 as promoting out of order, just earlier.
            foreach (string fileName in obsolete)
            {
                if (!SitemapNames.IsOwned(fileName))
                {
                    // Refuse to delete anything outside this service's own file set. The
                    // prefix may be shared, and a bug in the obsolete-set calculation must
                    // not be able to empty someone else's bucket.
                    logger.LogWarning(
                        "Refusing to delete {File}: it is not part of this service's sitemap set.",
                        fileName);
                    continue;
                }

                await s3.DeleteObjectAsync(R2.Bucket, LiveKey(fileName), cancellationToken);
                logger.LogInformation("Removed superseded sitemap file {File}.", fileName);
            }

            // ── 4. Staging swept ────────────────────────────────────────────────
            foreach (PendingUpload upload in changed)
            {
                try
                {
                    await s3.DeleteObjectAsync(
                        R2.Bucket, StagingKey(upload.FileName), cancellationToken);
                }
                catch (AmazonS3Exception ex)
                {
                    // Leftover staging objects are inert — nothing routes to that prefix and
                    // the next rebuild overwrites them. Not worth failing a committed swap.
                    logger.LogDebug(ex, "Could not clean staging key for {File}.", upload.FileName);
                }
            }

            _committed = true;

            DiagnosticsConfig.SitemapSwaps.Add(1);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (Stream stream in Enumerable.Reverse(_open))
            {
                try
                {
                    await stream.DisposeAsync();
                }
                catch (IOException)
                {
                    // Best effort: the temp files are removed below regardless.
                }
            }

            _open.Clear();

            if (!_committed && _pending.Count > 0)
            {
                // Said out loud. An abandoned stage is the SAFE outcome — the live set is
                // untouched — but it is also the signature of a rebuild that failed, and the
                // only symptom otherwise is a sitemap that quietly stops changing.
                logger.LogWarning(
                    "Sitemap stage abandoned without commit; {Count} staged file(s) discarded. "
                    + "The live set is unchanged.", _pending.Count);
            }

            foreach (PendingUpload upload in _pending)
            {
                try
                {
                    File.Delete(upload.TempPath);
                }
                catch (IOException)
                {
                    // A leaked temp file is a disk-space nuisance, not a correctness problem,
                    // and the container filesystem is ephemeral.
                }
            }

            _pending.Clear();
        }

        private async Task PutAsync(PendingUpload upload, string key, CancellationToken ct)
        {
            await using FileStream body = File.OpenRead(upload.TempPath);

            PutObjectRequest request = new()
            {
                BucketName = R2.Bucket,
                Key = key,
                InputStream = body,
                ContentType = upload.ContentType,

                // DO NOT REMOVE. This flag looks like a no-op and is not.
                //
                // The SDK defaults it to true, which signs the body as a streaming chunked
                // payload and sends `x-amz-content-sha256:
                // STREAMING-AWS4-HMAC-SHA256-PAYLOAD`. Cloudflare R2 does not implement that
                // signing mode and rejects the request outright — every upload this service
                // ever attempted against a real bucket failed with "not implemented", while
                // the staged set was correctly discarded and the cron loop carried on. False
                // to send an ordinary signed payload instead, which R2 accepts.
                //
                // Nothing in the test suite can catch its removal over the wire: the fake
                // bucket is in-process and never signs anything, and MinIO — the dev stand-in
                // — implements the streaming mode, so it accepts the request either way. The
                // failure appears only against R2. SitemapSinkTests asserts on the flag
                // itself for that reason.
                //
                // NOT DisablePayloadSigning. That would work too, by sending UNSIGNED-PAYLOAD,
                // but it drops the body integrity check and the SDK requires HTTPS for it —
                // which the MinIO dev stack, on plain http://minio:9000, does not use. This
                // keeps the payload signed and works against both.
                UseChunkEncoding = false,

                Headers =
                {
                    CacheControl = options.CacheControl,
                },
            };

            if (R2.Compress)
            {
                // Content-Encoding, not a content type of its own. Google fetches
                // `sitemap-jobs-1.xml.gz` and expects XML inside a gzip envelope; declaring
                // the type as application/gzip would have some clients treat it as an opaque
                // download rather than decoding it.
                request.Headers.ContentEncoding = "gzip";
            }

            // The checksum rides on the object so a bucket restored from backup, or written
            // by an older build, is still comparable without downloading it.
            request.Metadata.Add(ChecksumMetadataKey, upload.Checksum ?? string.Empty);

            await s3.PutObjectAsync(request, ct);

            DiagnosticsConfig.SitemapUploads.Add(1,
                new KeyValuePair<string, object?>("file", upload.FileName));
        }

        /// <summary>
        /// Server-side copy from staging to the live key. No bytes traverse the pod, so the
        /// swap window does not grow with the corpus.
        /// </summary>
        private async Task PromoteAsync(string fileName, CancellationToken ct)
        {
            CopyObjectRequest request = new()
            {
                SourceBucket = R2.Bucket,
                SourceKey = StagingKey(fileName),
                DestinationBucket = R2.Bucket,
                DestinationKey = LiveKey(fileName),
                MetadataDirective = S3MetadataDirective.COPY,
            };

            await s3.CopyObjectAsync(request, ct);
        }

        private string LiveKey(string fileName) => R2.Prefix + StoredName(fileName);

        private string StagingKey(string fileName) =>
            R2.Prefix + R2.StagingPrefix + StoredName(fileName);

        /// <summary>Adds the compression suffix, if any, to the logical file name.</summary>
        private string StoredName(string fileName) =>
            R2.Compress && fileName.EndsWith(".xml", StringComparison.Ordinal)
                ? fileName + ".gz"
                : fileName;

        /// <summary>One file on its way to the bucket.</summary>
        private sealed class PendingUpload(string fileName, string contentType)
        {
            public string FileName { get; } = fileName;

            public string ContentType { get; } = contentType;

            public string TempPath { get; } = Path.Combine(
                Path.GetTempPath(), $"kariyer-seo-{Guid.NewGuid():N}.tmp");

            /// <summary>
            /// Set by the caller once the writer has finished, so it can be stored as object
            /// metadata. Null until then — the checksum is only known after the last byte.
            /// </summary>
            public string? Checksum { get; set; }

            /// <summary>Verified byte-identical to the live object; skipped entirely.</summary>
            public bool Unchanged { get; set; }
        }

        /// <summary>
        /// Records the checksum the writer computed for a file already opened through
        /// <see cref="OpenWrite"/>.
        ///
        /// Separate from <c>OpenWrite</c> because the value does not exist yet at that point:
        /// it is produced by streaming the document, which is the whole reason the writer
        /// hashes on the way past instead of re-reading afterwards.
        /// </summary>
        public void RecordChecksum(string fileName, string checksum)
        {
            PendingUpload? upload = _pending.FirstOrDefault(u => u.FileName == fileName);

            if (upload is not null)
            {
                upload.Checksum = checksum;
            }
        }

        public void MarkUnchanged(string fileName)
        {
            PendingUpload? upload = _pending.FirstOrDefault(u => u.FileName == fileName);

            if (upload is not null)
            {
                upload.Unchanged = true;
            }
        }
    }
}
