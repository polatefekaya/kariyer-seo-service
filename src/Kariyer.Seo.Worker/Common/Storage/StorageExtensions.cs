using Amazon.Runtime;
using Amazon.S3;
using Kariyer.Seo.Domain.Ports;
using Kariyer.Seo.Worker.Common.Configuration;
using Kariyer.Seo.Worker.Common.Facets;
using Microsoft.Extensions.Options;

namespace Kariyer.Seo.Worker.Common.Storage;

/// <summary>Wires the R2 client and the facet manifest client.</summary>
public static class StorageExtensions
{
    public static IServiceCollection AddSitemapSink(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IAmazonS3>(sp =>
        {
            R2Options r2 = sp.GetRequiredService<IOptions<SeoOptions>>().Value.R2;

            AmazonS3Config config = new()
            {
                ServiceURL = r2.Endpoint,

                // R2 serves everything from one endpoint and does not do virtual-hosted
                // bucket subdomains, so path-style addressing is required rather than
                // preferred: without it the SDK builds `https://bucket.<account>.
                // r2.cloudflarestorage.com/key`, which does not resolve.
                ForcePathStyle = true,

                // R2 ignores the region but the SDK refuses to sign without one, and 'auto'
                // is the value Cloudflare documents.
                AuthenticationRegion = "auto",
            };

            return new AmazonS3Client(
                new BasicAWSCredentials(r2.AccessKey, r2.SecretKey), config);
        });

        services.AddSingleton<ISitemapSink, SitemapSink>();

        return services;
    }

    public static IServiceCollection AddFacetManifest(this IServiceCollection services)
    {
        services.AddHttpClient<IFacetManifestSource, FacetManifestClient>(client =>
        {
            // Identifies us in the web app's access logs. Worth having: "who is fetching
            // facet-manifest.json every six hours" should be answerable without guessing.
            client.DefaultRequestHeaders.UserAgent.ParseAdd("KariyerZamaniSeoService/1.0");
        });

        return services;
    }
}
