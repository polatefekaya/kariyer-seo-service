namespace Kariyer.Seo.Domain.Urls;

/// <summary>
/// Validates a site-relative path before this service turns it into a URL it publishes.
///
/// Used for CMS landing pages and for the hand-listed static paths — anything whose path is
/// data rather than something this service composed itself.
///
/// <b>Why validate a value another service already validated.</b> <c>kariyer-cms-service</c>
/// normalises and checks paths on publish, and this duplicates part of that. It is worth it
/// because of what the output IS: the sitemap is the document Google reads as our own
/// statement about our own site. A malformed row — from a migration, a manual SQL fix, a bug
/// in a validator that shipped last week — must not be able to put an absolute URL, a
/// protocol-relative host, or a traversal into it. The check costs nothing and the failure it
/// prevents is invisible until it is expensive.
///
/// It deliberately does NOT re-shape anything: a path that fails is skipped, never "fixed".
/// Rewriting it here would produce a URL the CMS's own resolver would not serve, which is a
/// worse outcome than omitting the page.
/// </summary>
public static class PagePath
{
    /// <summary>
    /// Whether this path is safe to emit into a sitemap.
    /// </summary>
    public static bool IsPublishable(string? path) =>
        path is not null
        && path.Length > 0
        && path[0] == '/'

        // Protocol-relative: `//evil.example/x` is an absolute URL to another host once a
        // browser or crawler resolves it against our origin.
        && (path.Length == 1 || path[1] != '/')

        && !path.Contains("..", StringComparison.Ordinal)

        // A scheme anywhere means someone stored an absolute URL in a relative-path column.
        && !path.Contains("://", StringComparison.Ordinal)

        // Control characters and whitespace would be percent-encoded inconsistently by
        // whatever fetches the URL, producing a second address for the same page.
        && !path.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));
}
