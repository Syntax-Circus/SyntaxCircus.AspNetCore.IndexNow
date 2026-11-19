using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace SyntaxCircus.AspNetCore.IndexNow;

/// <summary>Registers the IndexNow service and its HTTP transport.</summary>
public static class IndexNowServiceCollectionExtensions
{
    public static IServiceCollection AddSyntaxCircusIndexNow(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<IndexNowOptions>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpClient<IIndexNowService, IndexNowService>(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(GuardedTransport.CreateHandler);
        return services;
    }
}
