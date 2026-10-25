using Contrapost.Consumers.Handlers;
using Contrapost.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Contrapost.Consumers;

public static class DependencyInjection
{
    /// <summary>The event consumers from design doc §9.3, each on its own queue.</summary>
    public static IServiceCollection AddConsumers(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddInfrastructure(configuration);
        services.AddMessaging(configuration, "contrapost-consumers");
        services.AddSingleton<InboxProcessor>();
        services.AddSingleton<StatementProjector>();
        services.AddSingleton<LowBalanceNotifier>();
        services.AddHostedService<QueueConsumer<StatementProjector>>();
        services.AddHostedService<QueueConsumer<LowBalanceNotifier>>();
        return services;
    }
}
