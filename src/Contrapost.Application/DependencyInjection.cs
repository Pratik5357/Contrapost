using Contrapost.Application.Accounts;
using Contrapost.Application.Transfers;
using Microsoft.Extensions.DependencyInjection;

namespace Contrapost.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<AccountService>();
        services.AddScoped<TransferService>();
        return services;
    }
}
