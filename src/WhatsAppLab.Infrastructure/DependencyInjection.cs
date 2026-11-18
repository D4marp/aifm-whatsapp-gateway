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
        // One client per number (session); the registry creates them on demand.
        services.AddSingleton<IWhatsAppClientRegistry, WhatsAppClientRegistry>();

        // Media files live outside the repo (default: %LOCALAPPDATA%\aifm-whatsapp-lab\media). Media:Root and Media:MaxMegabytes change that.
        services.AddSingleton(new MediaOptions
        {
            Root = config["Media:Root"] is { Length: > 0 } r ? r : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "aifm-whatsapp-lab", "media"),
            MaxBytes = (long.TryParse(config["Media:MaxMegabytes"], out var mb) && mb > 0 ? mb : 25) * 1024 * 1024
        });
        services.AddScoped<IWhatsAppMediaUseCase, WhatsAppMediaUseCase>();
        services.AddScoped<IWhatsAppSessionUseCase, WhatsAppSessionUseCase>();
        services.AddScoped<IWhatsAppSettingUseCase, WhatsAppSettingUseCase>();
        services.AddScoped<IWhatsAppGroupUseCase, WhatsAppGroupUseCase>();
        services.AddScoped<IWhatsAppOutboxUseCase, WhatsAppOutboxUseCase>();
        services.AddScoped<IWhatsAppInboundUseCase, WhatsAppInboundUseCase>();
        return services;
    }
}
