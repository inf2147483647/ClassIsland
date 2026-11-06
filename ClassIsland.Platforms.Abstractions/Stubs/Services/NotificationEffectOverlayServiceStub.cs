using Avalonia.Media;
using ClassIsland.Platforms.Abstraction.Services;
using ClassIsland.Shared.Enums;

namespace ClassIsland.Platforms.Abstraction.Stubs.Services;

/// <summary>
/// 不支持覆盖层特效的平台默认实现。
/// </summary>
public class NotificationEffectOverlayServiceStub : INotificationEffectOverlayService
{
    internal NotificationEffectOverlayServiceStub()
    {
    }

    /// <inheritdoc />
    public bool IsSupported => false;

    /// <inheritdoc />
    public void Prewarm(int fpsLimit, NotificationEffectQuality quality)
    {
    }

    /// <inheritdoc />
    public Task PlayAsync(nint mainWindowHandle, Color color, int fpsLimit, NotificationEffectQuality quality)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Shutdown()
    {
    }
}
