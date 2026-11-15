using WhatsAppLab.Application;
using WhatsAppLab.Domain;

namespace WhatsAppLab.Api;

/// <summary>Runs forever in the background: sends what is due in the outbox, and records incoming group messages.</summary>
public class WhatsAppDispatcherService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IWhatsAppClient _client;
    private readonly ILogger<WhatsAppDispatcherService> _logger;
    private readonly int _seconds;

    public WhatsAppDispatcherService(IServiceScopeFactory scopes, IWhatsAppClient client, IConfiguration config, ILogger<WhatsAppDispatcherService> logger)
    {
        _scopes = scopes; _client = client; _logger = logger;
        _seconds = Math.Max(1, config.GetValue("Lab:DispatchEverySeconds", 2));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _client.MessageReceived += async m =>
        {
            using var scope = _scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IWhatsAppInboundUseCase>().RecordAsync(m);
        };

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                if (_client is IWhatsAppSessionControl control)
                {
                    var setting = await scope.ServiceProvider.GetRequiredService<IWhatsAppSettingUseCase>().GetAsync();
                    control.SetReceiving(setting != null && setting.IsEnabled && setting.ReceiveEnabled);
                }
                await scope.ServiceProvider.GetRequiredService<IWhatsAppOutboxUseCase>().DispatchDueAsync(stoppingToken);
            }
            catch (Exception ex) { _logger.LogError(ex, "Dispatcher tick failed."); }
            await Task.Delay(TimeSpan.FromSeconds(_seconds), stoppingToken).ContinueWith(_ => { });
        }
    }
}

/// <summary>What Hangfire runs on a schedule. It does NOT send: it only puts a row in the outbox, so every message goes through the same limits.</summary>
public class AnnouncementJob
{
    private readonly IWhatsAppOutboxUseCase _outbox;
    public AnnouncementJob(IWhatsAppOutboxUseCase outbox) { _outbox = outbox; }
    public Task Run(string groupJid, string body) => _outbox.EnqueueAsync(groupJid, body, null, OutboundSource.Scheduled, "hangfire");
}
