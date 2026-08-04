using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using NSubstitute;

namespace Kariyer.Seo.IntegrationTests.Storage;

/// <summary>
/// An in-memory bucket behind a real <see cref="IAmazonS3"/>, so the REAL
/// <c>SitemapSink</c> can be exercised without R2 or a container.
///
/// <b>Why not MinIO.</b> The same reasoning as <c>FakeSitemapSink</c>: what needs testing is
/// not "can we speak S3" — the AWS SDK can — but what this service puts in the bucket and in
/// which order. Against MinIO an ordering bug produces a passing test with a race in it, and
/// the cost of running a second container per test is paid on every CI run forever.
///
/// <b>Why not a hand-written IAmazonS3.</b> The interface has some two hundred members, of
/// which the sink calls five. The state lives in this class and a substitute is wired to it,
/// which keeps the modelled behaviour readable and leaves the rest of the interface alone.
///
/// The behaviour that is modelled rather than stubbed — a COPY carrying the source object's
/// metadata, a DELETE succeeding on a key that is not there, a LIST that terminates — is
/// modelled because the sink depends on it. Anything less and the checksum short-circuit
/// would silently never fire.
/// </summary>
internal sealed class FakeS3Bucket
{
    private readonly Dictionary<string, StoredObject> _objects = new(StringComparer.Ordinal);
    private readonly List<Call> _calls = [];
    private readonly Lock _gate = new();

    public FakeS3Bucket()
    {
        IAmazonS3 s3 = Substitute.For<IAmazonS3>();

        s3.PutObjectAsync(Arg.Any<PutObjectRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(Put(call.Arg<PutObjectRequest>()!)));

        s3.CopyObjectAsync(Arg.Any<CopyObjectRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(Copy(call.Arg<CopyObjectRequest>()!)));

        // The three-argument overloads, which are the ones the sink calls. IAmazonS3 also
        // declares four-argument versions of both; matching on exactly three Arg.Any binds
        // this pair unambiguously.
        s3.DeleteObjectAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(Delete(call.ArgAt<string>(1))));

        s3.ListObjectsV2Async(Arg.Any<ListObjectsV2Request>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(List(call.Arg<ListObjectsV2Request>()!)));

        s3.GetObjectMetadataAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(Head(call.ArgAt<string>(1))));

        Client = s3;
    }

    public IAmazonS3 Client { get; }

    /// <summary>Everything currently in the bucket, keyed by full object key.</summary>
    public IReadOnlyDictionary<string, StoredObject> Objects
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, StoredObject>(_objects, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>
    /// Every mutating request in the order it was issued.
    ///
    /// Recorded rather than inferred from the final state, because the ORDER is the atomicity
    /// claim of PLAN §6.3 — children promoted before the index, obsolete files deleted only
    /// after it — and a final-state assertion cannot see it.
    /// </summary>
    public IReadOnlyList<Call> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    public IEnumerable<string> KeysOf(string verb) =>
        Calls.Where(c => c.Verb == verb).Select(c => c.Key);

    /// <summary>Forgets the call log but keeps the objects — for asserting on a second run.</summary>
    public void ClearCalls()
    {
        lock (_gate)
        {
            _calls.Clear();
        }
    }

    private PutObjectResponse Put(PutObjectRequest request)
    {
        // Copied HERE, synchronously, while the call is still on the stack.
        // request.InputStream is a FileStream over a temp file that the sink disposes the
        // instant this returns and deletes when the stage tears down; a fake that stashed the
        // Stream and read it from an assertion would be reading a closed handle over a file
        // that no longer exists.
        using MemoryStream buffer = new();
        request.InputStream.CopyTo(buffer);

        // MetadataCollection normalises its keys to `x-amz-meta-…`, and does so idempotently,
        // so what comes out of Keys goes straight back in on the HEAD side and round-trips.
        // Getting this wrong costs no exception — GetLiveChecksumsAsync would simply return
        // nothing, forever, and every rebuild would re-upload the whole set.
        Dictionary<string, string> metadata = request.Metadata.Keys
            .ToDictionary(key => key, key => request.Metadata[key], StringComparer.Ordinal);

        lock (_gate)
        {
            _objects[request.Key] = new StoredObject(
                buffer.ToArray(),
                request.ContentType,
                request.Headers.ContentEncoding,
                request.Headers.CacheControl,
                metadata);

            _calls.Add(new Call("PUT", request.Key));
        }

        return new PutObjectResponse { HttpStatusCode = HttpStatusCode.OK };
    }

    private CopyObjectResponse Copy(CopyObjectRequest request)
    {
        lock (_gate)
        {
            if (!_objects.TryGetValue(request.SourceKey, out StoredObject? source))
            {
                // R2 answers NoSuchKey. A fake that shrugged here would let a sink that
                // promoted before it staged pass this suite.
                throw new AmazonS3Exception($"The specified key does not exist: {request.SourceKey}")
                {
                    ErrorCode = "NoSuchKey",
                    StatusCode = HttpStatusCode.NotFound,
                };
            }

            // MetadataDirective.COPY means the destination inherits the source object whole,
            // seo-checksum included — which is what makes the next run's short-circuit work.
            _objects[request.DestinationKey] = source;

            _calls.Add(new Call("COPY", request.DestinationKey));
        }

        return new CopyObjectResponse { HttpStatusCode = HttpStatusCode.OK };
    }

    private DeleteObjectResponse Delete(string key)
    {
        lock (_gate)
        {
            // Deleting a key that is not there is a 204 in S3, not an error. The sink sweeps
            // staging keys unconditionally and derives its obsolete set from two independent
            // sources, so it relies on that.
            _objects.Remove(key);

            _calls.Add(new Call("DELETE", key));
        }

        return new DeleteObjectResponse { HttpStatusCode = HttpStatusCode.NoContent };
    }

    private ListObjectsV2Response List(ListObjectsV2Request request)
    {
        lock (_gate)
        {
            List<S3Object> matches =
            [
                .. _objects.Keys
                    .Where(k => k.StartsWith(request.Prefix ?? string.Empty, StringComparison.Ordinal))
                    .OrderBy(k => k, StringComparer.Ordinal)
                    .Select(k => new S3Object { BucketName = request.BucketName, Key = k }),
            ];

            // One page, and IsTruncated false. The sink pages in a do/while on this flag; a
            // fake that set it without advancing the continuation token would spin forever.
            return new ListObjectsV2Response
            {
                S3Objects = matches,
                IsTruncated = false,
                HttpStatusCode = HttpStatusCode.OK,
            };
        }
    }

    private GetObjectMetadataResponse Head(string key)
    {
        lock (_gate)
        {
            if (!_objects.TryGetValue(key, out StoredObject? stored))
            {
                throw new AmazonS3Exception($"Not found: {key}")
                {
                    ErrorCode = "NoSuchKey",
                    StatusCode = HttpStatusCode.NotFound,
                };
            }

            GetObjectMetadataResponse response = new() { HttpStatusCode = HttpStatusCode.OK };

            // Metadata has an internal setter, so Add is the only way to populate it.
            foreach ((string name, string value) in stored.Metadata)
            {
                response.Metadata.Add(name, value);
            }

            return response;
        }
    }

    /// <summary>One mutating request against the bucket.</summary>
    internal sealed record Call(string Verb, string Key);

    /// <summary>One object in the bucket, as it was stored.</summary>
    internal sealed record StoredObject(
        byte[] Body,
        string? ContentType,
        string? ContentEncoding,
        string? CacheControl,
        IReadOnlyDictionary<string, string> Metadata);
}
