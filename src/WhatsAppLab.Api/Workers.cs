using System.Collections.Concurrent;
using WhatsAppLab.Application;
using WhatsAppLab.Domain;

namespace WhatsAppLab.Api;

/// <summary>
/// Runs forever in the background. Every tick, for EVERY number (session): sends what is due in its outbox, and records its incoming group messages.
/// Each number has its own client, settings and pacing, so one number's limits never hold back another.
/// </summary>
public class WhatsAppDispatcherService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IWhatsAppClientRegistry _registry;
    private readonly ILogger<WhatsAppDispatcherService> _logger;
    private readonly int _seconds;
    private readonly ConcurrentDictionary<Guid, bool> _hooked = new();

    public WhatsAppDispatcherService(IServiceScopeFactory scopes, IWhatsAppClientRegistry registry, IConfiguration config, ILogger<WhatsAppDispatcherService> logger)
    {
        _scopes = scopes; _registry = registry; _logger = logger;
        _seconds = Math.Max(1, config.GetValue("Lab:DispatchEverySeconds", 2));
    }

    private void Hook(Guid sessionId, IWhatsAppClient client)
    {
        if (!_hooked.TryAdd(sessionId, true)) return;
        client.MessageReceived += async m =>
        {
            using var scope = _scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IWhatsAppInboundUseCase>().RecordAsync(sessionId, m);
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _registry.ClientCreated += Hook;   // clients created later (a number added while running) are hooked as they appear

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var sessions = await scope.ServiceProvider.GetRequiredService<IWhatsAppSessionUseCase>().ListAsync();
                foreach (var session in sessions)
                {
                    try
                    {
                        var client = _registry.Get(session);
                        Hook(session.Id, client);   // also covers a client that was created before the dispatcher started
                        var setting = await scope.ServiceProvider.GetRequiredService<IWhatsAppSettingUseCase>().GetAsync(session.Id);
                        if (client is IWhatsAppSessionControl control)
                        {
                            control.SetReceiving(setting != null && setting.IsEnabled && setting.ReceiveEnabled);
                            control.SetAllowedGroups(await scope.ServiceProvider.GetRequiredService<IWhatsAppGroupUseCase>().AllowedNamesAsync(session.Id));
                        }
                        await scope.ServiceProvider.GetRequiredService<IWhatsAppOutboxUseCase>().DispatchDueAsync(session.Id, stoppingToken);
                    }
                    catch (Exception ex) { _logger.LogError(ex, "Dispatcher tick failed for session {Session}.", session.Name); }   // one number failing must not stop the others
                }
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
    private readonly IWhatsAppSessionUseCase _sessions;
    public AnnouncementJob(IWhatsAppOutboxUseCase outbox, IWhatsAppSessionUseCase sessions) { _outbox = outbox; _sessions = sessions; }
    public Task Run(Guid sessionId, string groupJid, string body) => _outbox.EnqueueAsync(sessionId, groupJid, body, null, OutboundSource.Scheduled, "hangfire");
    /// <summary>Jobs saved before several numbers existed have no session id: they go to the first number.</summary>
    public async Task Run(string groupJid, string body)
    {
        var s = await _sessions.ResolveAsync(null);
        if (s != null) await Run(s.Id, groupJid, body);
    }
}
