using System.Text;

namespace Kariyer.Seo.Domain.Indexation;

/// <summary>
/// Turkish-aware case and diacritic folding, ported from the web app's
/// <c>src/seo/facets/slugify.ts</c> so both sides agree on what "the same value" means.
///
/// Two reasons this cannot be <c>ToLowerInvariant</c>:
///
/// <b>Turkish dotted/dotless I.</b> <c>İ</c> and <c>I</c> lowercase inconsistently — under
/// invariant globalization <c>'İ'.ToLowerInvariant()</c> yields <c>i</c> followed by a
/// COMBINING DOT ABOVE, a two-char sequence that will never equal the <c>i</c> in a URL
/// slug. <c>İstanbul</c> would stop matching <c>istanbul</c>, and the largest city in the
/// country would drop out of the filter sitemap.
///
/// <b>The service runs with InvariantGlobalization=true.</b> There is no <c>tr-TR</c>
/// culture in the container to defer to even if we wanted one. Mapping the eight letters
/// explicitly is not a workaround for that — it is the only definition that is identical in
/// a unit test, in the container, and in the browser running the SPA.
/// </summary>
public static class TurkishFold
{
    /// <summary>
    /// Folds arbitrary Turkish text to a comparison key:
    /// <c>"Bilişim - İnternet"</c> → <c>"bilisim-internet"</c>.
    ///
    /// Identical in behaviour to the web app's <c>foldSlug</c>, including collapsing runs of
    /// non-alphanumerics to a single hyphen and trimming leading/trailing ones. That is what
    /// makes a folded <c>company_job</c> value directly comparable to a slug taken out of a
    /// facet path.
    /// </summary>
    public static string Slug(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        StringBuilder builder = new(input.Length);
        bool pendingHyphen = false;

        foreach (char raw in input)
        {
            char mapped = Map(raw);

            if (IsSlugChar(mapped))
            {
                if (pendingHyphen && builder.Length > 0)
                {
                    builder.Append('-');
                }

                pendingHyphen = false;
                builder.Append(mapped);
            }
            else
            {
                // Deferred rather than appended immediately, so a run of separators
                // collapses and a trailing run disappears without a second pass.
                pendingHyphen = true;
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Case- and diacritic-insensitive "contains", over folded forms.
    ///
    /// This mirrors what the jobs API actually does. The Node job repository matches
    /// <c>department</c> and <c>position</c> with <c>iLike '%value%'</c>, so a filter value
    /// of <c>Bilişim</c> selects rows whose department is <c>Bilişim - İnternet</c>. Using
    /// equality here instead would count fewer jobs than the page itself shows, gate a facet
    /// out of the sitemap that the SPA marks <c>index,follow</c>, and put the two into
    /// permanent disagreement about the same page.
    /// </summary>
    public static bool Contains(string? haystack, string? needle)
    {
        string foldedNeedle = Slug(needle);

        if (foldedNeedle.Length == 0)
        {
            return false;
        }

        return Slug(haystack).Contains(foldedNeedle, StringComparison.Ordinal);
    }

    /// <summary>Exact match over folded forms. Used for province, which the API matches
    /// exactly (<c>Op.in</c>), not by substring.</summary>
    public static bool Equal(string? left, string? right)
    {
        string foldedLeft = Slug(left);
        return foldedLeft.Length > 0 && foldedLeft == Slug(right);
    }

    private static bool IsSlugChar(char c) => c is (>= 'a' and <= 'z') or (>= '0' and <= '9');

    /// <summary>
    /// The Turkish letters, mapped BEFORE any lowercasing — exactly as the TypeScript does,
    /// and for exactly the same reason: doing it after would already have lost the
    /// dotted/dotless distinction.
    ///
    /// ASCII uppercase is folded here too so the whole function needs no culture at all.
    /// </summary>
    private static char Map(char c) => c switch
    {
        'ç' or 'Ç' => 'c',
        'ğ' or 'Ğ' => 'g',
        'ı' or 'I' or 'İ' or 'i' => 'i',
        'ö' or 'Ö' => 'o',
        'ş' or 'Ş' => 's',
        'ü' or 'Ü' => 'u',
        'â' or 'Â' => 'a',
        'î' or 'Î' => 'i',
        'û' or 'Û' => 'u',
        >= 'A' and <= 'Z' => (char)(c + 32),
        _ => c,
    };
}
