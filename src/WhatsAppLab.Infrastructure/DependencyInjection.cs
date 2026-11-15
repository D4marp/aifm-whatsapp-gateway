using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WhatsAppLab.Application;

namespace WhatsAppLab.Infrastructure;

// Like Infrastructure/DependencyInjection.cs in the ERP: one place that wires interfaces to classes.
public static class DependencyInjection
{
    public static IServiceCollection AddLabInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.AddDbContext<LabDbContext>(o => o.UseSqlServer(config.GetConnectionString("DefaultConnection")));
        services.AddScoped<IApplicationDbContext>(sp => sp.GetRequiredService<LabDbContext>());

        // WhatsApp:Client = Fake (default) or Real. Switching is a configuration change, not a code change.
        if (string.Equals(config["WhatsApp:Client"], "Real", StringComparison.OrdinalIgnoreCase))
            services.AddSingleton<IWhatsAppClient, RealWhatsAppClient>();
        else
        {
            services.AddSingleton<FakeWhatsAppClient>();
            services.AddSingleton<IWhatsAppClient>(sp => sp.GetRequiredService<FakeWhatsAppClient>());
        }

        services.AddScoped<IWhatsAppSettingUseCase, WhatsAppSettingUseCase>();
        services.AddScoped<IWhatsAppGroupUseCase, WhatsAppGroupUseCase>();
        services.AddScoped<IWhatsAppOutboxUseCase, WhatsAppOutboxUseCase>();
        services.AddScoped<IWhatsAppInboundUseCase, WhatsAppInboundUseCase>();
        return services;
    }
}
