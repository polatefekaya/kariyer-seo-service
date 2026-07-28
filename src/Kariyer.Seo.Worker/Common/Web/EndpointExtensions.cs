using System.Reflection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Kariyer.Seo.Worker.Common.Web;

/// <summary>
/// Discovers and maps every <see cref="IEndpoint"/> in the assembly, under a single
/// route group. Keeps slices self-contained: no central registry file to edit, so a
/// feature folder can be added or deleted without touching shared code.
/// </summary>
public static class EndpointExtensions
{
    private const string RoutePrefix = "/api/seo";

    public static IServiceCollection AddEndpoints(this IServiceCollection services, Assembly assembly)
    {
        ServiceDescriptor[] descriptors = assembly
            .DefinedTypes
            .Where(type => type is { IsAbstract: false, IsInterface: false }
                        && type.IsAssignableTo(typeof(IEndpoint)))
            .Select(type => ServiceDescriptor.Transient(typeof(IEndpoint), type))
            .ToArray();

        services.TryAddEnumerable(descriptors);
        return services;
    }

    public static IApplicationBuilder MapEndpoints(this WebApplication app)
    {
        RouteGroupBuilder group = app.MapGroup(RoutePrefix);

        foreach (IEndpoint endpoint in app.Services.GetRequiredService<IEnumerable<IEndpoint>>())
        {
            endpoint.MapEndpoint(group);
        }

        return app;
    }
}
