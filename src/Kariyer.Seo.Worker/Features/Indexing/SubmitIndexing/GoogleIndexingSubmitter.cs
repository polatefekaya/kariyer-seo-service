using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kariyer.Messaging.Contracts.Seo;
using Kariyer.Seo.Domain.Urls;
using Kariyer.Seo.Worker.Common.Configuration;
using Kariyer.Seo.Worker.Common.Telemetry;
using MassTransit;
using Microsoft.Extensions.Options;

namespace Kariyer.Seo.Worker.Features.Indexing.SubmitIndexing;

/// <summary>
/// Calls the Google Indexing API and emits <c>JobUrlIndexingSubmittedEvent</c>.
///
/// <b>It never throws.</b> Every call site is downstream of a committed state change, so an
/// exception here would fault the message and replay a removal that has already happened, to
/// retry a notification that is best-effort by design. Failures are logged, counted and
/// published as <c>Success=false</c> instead — which is also why the event is emitted for
/// failures: a submission that silently failed is otherwise indistinguishable from one never
/// attempted.
///
/// <b>The quota is enforced locally.</b> Google's default is 200 URLs per day per project,
/// shared with anything else using that project. Left to Google's 429, a batch of expiries
/// would burn the whole day's allowance in a minute and a genuinely urgent submission that
/// afternoon would be refused. A local counter fails the cheap way instead.
///
/// The OAuth assertion is signed here rather than through the Google client libraries, which
/// would pull a large dependency tree into a service that makes one kind of request. The JWT
/// bearer flow for a service account is a signed assertion exchanged for an access token —
/// small enough to be worth writing, and visible enough to be worth reviewing.
/// </summary>
public sealed class GoogleIndexingSubmitter(
    HttpClient http,
    IPublishEndpoint publisher,
    IOptions<IndexingOptions> options,
    IOptions<SeoOptions> seo,
    TimeProvider clock,
    ILogger<GoogleIndexingSubmitter> logger) : IIndexingSubmitter, IDisposable
{
    private const string Scope = "https://www.googleapis.com/auth/indexing";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";

    /// <summary>Serialises token refresh, which is async and must not block a thread.</summary>
    private readonly SemaphoreSlim _tokenGate = new(1, 1);

    /// <summary>
    /// Guards the quota counter. A separate, plain lock rather than reusing the token
    /// semaphore: the counter is touched synchronously on every submission, and taking an
    /// async gate for a two-field increment would serialise submissions behind token refresh
    /// for no reason.
    /// </summary>
    private readonly Lock _quotaGate = new();

    private ServiceAccountKey? _key;
    private string? _accessToken;
    private DateTimeOffset _tokenExpiresAt;
    private DateOnly _quotaDay;
    private int _submittedToday;

    public async Task SubmitAsync(
        string jobUid, string slug, IndexingAction action, CancellationToken ct)
    {
        string url = JobUrl.For(seo.Value.SiteUrl, slug);
        string verb = action is IndexingAction.Deleted
            ? IndexingOptions.Actions.Deleted
            : IndexingOptions.Actions.Updated;

        bool success = false;

        try
        {
            if (!TryConsumeQuota())
            {
                logger.LogWarning(
                    "Google Indexing API daily quota of {Quota} exhausted; {Action} for {Url} "
                    + "not submitted. The sitemap lastmod remains the signal.",
                    options.Value.DailyQuota, verb, url);
            }
            else
            {
                success = await PublishAsync(url, verb, ct);
            }
        }
        catch (Exception ex)
        {
            // Broad on purpose, and this is the one place in the service where that is right:
            // the contract of this method is that it cannot throw, because throwing would
            // replay a committed removal.
            logger.LogWarning(ex, "Google Indexing API submission failed for {Url}.", url);
        }

        DiagnosticsConfig.IndexingSubmissions.Add(1,
            new KeyValuePair<string, object?>("action", verb),
            new KeyValuePair<string, object?>("outcome", success ? "success" : "failure"));

        // Published for failures too. Without that, "we never told Google" and "we told
        // Google and it refused" look identical from outside, and only one of them is a
        // problem someone should act on.
        await publisher.Publish(
            new JobUrlIndexingSubmittedEvent
            {
                MessageId = $"{jobUid}:{verb}:{clock.GetUtcNow():O}",
                JobUid = jobUid,
                Slug = slug,
                Action = verb,
                Success = success,
                SubmittedAt = clock.GetUtcNow(),
            },
            ct);
    }

    private async Task<bool> PublishAsync(string url, string verb, CancellationToken ct)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.TimeoutSeconds));

        string token = await GetAccessTokenAsync(timeout.Token);

        using HttpRequestMessage request = new(HttpMethod.Post, IndexingOptions.EndpointUrl)
        {
            Content = JsonContent.Create(new { url, type = verb }),
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await http.SendAsync(request, timeout.Token);

        if (response.IsSuccessStatusCode)
        {
            logger.LogDebug("Submitted {Action} for {Url} to the Google Indexing API.", verb, url);
            return true;
        }

        string body = await response.Content.ReadAsStringAsync(timeout.Token);

        logger.LogWarning(
            "Google Indexing API returned {Status} for {Action} {Url}: {Body}",
            (int)response.StatusCode, verb, url, body);

        return false;
    }

    /// <summary>
    /// Exchanges a signed service-account assertion for an access token, caching it until
    /// shortly before it expires.
    ///
    /// The one-minute safety margin is not decoration: a token that expires between the
    /// check and the request arriving at Google fails the submission, and this path only ever
    /// runs on the tail of an already-committed change where a retry is not free.
    /// </summary>
    private async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        await _tokenGate.WaitAsync(ct);

        try
        {
            DateTimeOffset now = clock.GetUtcNow();

            if (_accessToken is not null && now < _tokenExpiresAt - TimeSpan.FromMinutes(1))
            {
                return _accessToken;
            }

            _key ??= LoadKey(options.Value.CredentialsPath);

            string assertion = BuildAssertion(_key, now);

            using FormUrlEncodedContent form = new(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                ["assertion"] = assertion,
            });

            using HttpResponseMessage response = await http.PostAsync(TokenEndpoint, form, ct);
            response.EnsureSuccessStatusCode();

            TokenResponse? token =
                await response.Content.ReadFromJsonAsync<TokenResponse>(ct);

            if (token?.AccessToken is null)
            {
                throw new InvalidOperationException("Google returned no access token.");
            }

            _accessToken = token.AccessToken;
            _tokenExpiresAt = now.AddSeconds(token.ExpiresIn);

            return _accessToken;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    private static ServiceAccountKey LoadKey(string path)
    {
        ServiceAccountKey? key = JsonSerializer.Deserialize<ServiceAccountKey>(
            File.ReadAllText(path));

        if (key?.ClientEmail is null || key.PrivateKey is null)
        {
            throw new InvalidOperationException(
                $"The service-account key at '{path}' is missing client_email or private_key.");
        }

        return key;
    }

    /// <summary>Builds and RS256-signs the JWT bearer assertion.</summary>
    private static string BuildAssertion(ServiceAccountKey key, DateTimeOffset now)
    {
        long issuedAt = now.ToUnixTimeSeconds();

        string header = Base64Url("""{"alg":"RS256","typ":"JWT"}"""u8.ToArray());

        string payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iss = key.ClientEmail,
            scope = Scope,
            aud = TokenEndpoint,
            iat = issuedAt,

            // One hour is the maximum Google accepts for this assertion, and it is only ever
            // used once, immediately, to obtain a token.
            exp = issuedAt + 3600,
        }));

        string signingInput = header + "." + payload;

        using RSA rsa = RSA.Create();
        rsa.ImportFromPem(key.PrivateKey);

        byte[] signature = rsa.SignData(
            Encoding.UTF8.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        return signingInput + "." + Base64Url(signature);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// Takes one unit of the daily quota, resetting at UTC midnight.
    ///
    /// In-memory, so a restart resets it. That is the deliberate trade: persisting it would
    /// add a table and a write to the hot path of every expiry to protect a limit whose only
    /// consequence is a 429 on a best-effort call. Under-counting after a restart risks a few
    /// refused submissions; over-engineering it costs every expiry a round trip.
    /// </summary>
    private bool TryConsumeQuota()
    {
        DateOnly today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

        lock (_quotaGate)
        {
            if (today != _quotaDay)
            {
                _quotaDay = today;
                _submittedToday = 0;
            }

            if (_submittedToday >= options.Value.DailyQuota)
            {
                return false;
            }

            _submittedToday++;
            return true;
        }
    }

    public void Dispose() => _tokenGate.Dispose();

    private sealed class ServiceAccountKey
    {
        [JsonPropertyName("client_email")]
        public string? ClientEmail { get; init; }

        [JsonPropertyName("private_key")]
        public string? PrivateKey { get; init; }
    }

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; init; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; init; }
    }
}
