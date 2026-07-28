namespace Kariyer.Seo.Worker.Common.Web;

/// <summary>
/// Marker for a minimal-API endpoint that lives inside its feature slice rather than in
/// a shared controller. Implementations are discovered by assembly scan, so adding an
/// operation means adding one file to one folder and nothing else.
///
/// This service is a worker first, and it does not serve the sitemaps — Cloudflare does,
/// straight from R2 (PLAN §1). Its HTTP surface is therefore limited to health, metrics and
/// operator diagnostics: inspect the last rebuild, force a new one. There is no public API
/// here and no route that returns a sitemap.
/// </summary>
public interface IEndpoint
{
    void MapEndpoint(IEndpointRouteBuilder app);
}
