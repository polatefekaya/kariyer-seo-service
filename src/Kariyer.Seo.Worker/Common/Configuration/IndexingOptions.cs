using System.ComponentModel.DataAnnotations;

namespace Kariyer.Seo.Worker.Common.Configuration;

/// <summary>
/// The optional Google Indexing API slice (PLAN §4, §15 step 7).
///
/// Off by default, and that default is the honest one. Google's Indexing API is documented
/// as supporting <c>JobPosting</c> and livestream markup only, it is quota-limited (200
/// URLs/day by default), and it needs a service account that has been added as an owner of
/// the Search Console property. None of that can be inferred from an empty config file, so
/// enabling it is a deliberate act — and <c>Enabled</c> means USABLE: a true here without a
/// credentials file fails startup rather than failing quietly on every expiry, because a
/// submitter that never throws is indistinguishable from one that was never switched on.
/// </summary>
public sealed class IndexingOptions
{
    public const string SectionName = "Indexing";

    public bool Enabled { get; init; }

    /// <summary>
    /// Path to the Google service-account JSON key.
    ///
    /// A file path rather than the key inline, so the secret arrives as a mounted volume and
    /// never as an environment variable that every crash dump and <c>docker inspect</c>
    /// would carry.
    /// </summary>
    public string CredentialsPath { get; init; } = string.Empty;

    /// <summary>
    /// Daily submission ceiling.
    ///
    /// Enforced locally rather than left to Google's 429, because the quota is per project
    /// and shared with anything else using it: burning it on a batch of expiries would mean
    /// a genuinely urgent submission later that day is refused.
    /// </summary>
    [Range(1, 1_000_000)]
    public int DailyQuota { get; init; } = 200;

    [Range(1, 300)]
    public int TimeoutSeconds { get; init; } = 15;

    public const string EndpointUrl =
        "https://indexing.googleapis.com/v3/urlNotifications:publish";

    /// <summary>Google's action verbs.</summary>
    public static class Actions
    {
        public const string Updated = "URL_UPDATED";
        public const string Deleted = "URL_DELETED";
    }
}
