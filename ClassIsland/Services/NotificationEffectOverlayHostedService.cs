using System;
using System.Threading;
using System.Threading.Tasks;
using ClassIsland.Platforms.Abstraction;
using ClassIsland.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClassIsland.Services;

/// <summary>
/// 高性能提醒强调特效的后台服务：启动时预热覆盖层资源，退出时释放。
/// </summary>
public class NotificationEffectOverlayHostedService(
    SettingsService settingsService,
    ILogger<NotificationEffectOverlayHostedService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var settings = settingsService.Settings;
        if (!settings.IsHighPerformanceNotificationEffectEnabled ||
            !PlatformServices.NotificationEffectOverlayService.IsSupported)
        {
            return Task.CompletedTask;
        }

        try
        {
            PlatformServices.NotificationEffectOverlayService.Prewarm(
                settings.HighPerformanceNotificationEffectFpsLimit,
                settings.HighPerformanceNotificationEffectQuality);
            logger.LogInformation("高性能提醒强调特效覆盖层已预热");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "高性能提醒强调特效覆盖层预热失败");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            PlatformServices.NotificationEffectOverlayService.Shutdown();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "释放高性能提醒强调特效覆盖层失败");
        }

        return Task.CompletedTask;
    }
}
