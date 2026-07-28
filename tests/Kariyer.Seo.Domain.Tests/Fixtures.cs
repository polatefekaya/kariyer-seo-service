using System.Text;

namespace Kariyer.Seo.Domain.Tests;

/// <summary>
/// Loads and, on first run, WRITES the golden files.
///
/// The write-on-miss behaviour is deliberate and mirrors the freshness service's schema
/// test: a new golden is produced once, reviewed as a diff in the pull request, and
/// committed. After that any change to the XML shows up as a failing test and a readable
/// diff, which is the only way a "harmless" formatting change to a sitemap gets noticed
/// before Googlebot notices it.
/// </summary>
internal static class Fixtures
{
    public static string Path(string name) =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    /// <summary>
    /// Asserts that <paramref name="actual"/> matches the committed golden, creating it if
    /// it does not exist yet.
    /// </summary>
    public static void AssertMatches(string name, string actual)
    {
        string path = Path(name);

        if (!File.Exists(path))
        {
            // Written to the SOURCE tree, not just the output directory, so it can actually
            // be reviewed and committed rather than vanishing on the next clean.
            string source = SourcePath(name);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(source)!);
            File.WriteAllText(source, actual);

            Assert.Fail(
                $"Wrote a new golden fixture to {source}. Review the XML and commit it, then "
                + "re-run. A golden that appears without being read is worth nothing.");
        }

        string expected = File.ReadAllText(path).ReplaceLineEndings("\n");

        Assert.Equal(expected, actual.ReplaceLineEndings("\n"));
    }

    public static string Utf8(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    private static string SourcePath(string name)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null
               && !File.Exists(System.IO.Path.Combine(directory.FullName, "Kariyer.Seo.slnx")))
        {
            directory = directory.Parent;
        }

        string root = directory?.FullName
            ?? throw new InvalidOperationException("Repository root not found.");

        return System.IO.Path.Combine(
            root, "tests", "Kariyer.Seo.Domain.Tests", "Fixtures", name);
    }
}
