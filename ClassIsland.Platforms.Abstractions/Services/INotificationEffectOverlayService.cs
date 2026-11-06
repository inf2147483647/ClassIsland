using Avalonia.Media;
using ClassIsland.Shared.Enums;

namespace ClassIsland.Platforms.Abstraction.Services;

/// <summary>
/// 提醒强调特效的平台覆盖层服务。实现可以脱离 Avalonia 渲染管线播放特效，
/// 从而在性能有限的设备上保持流畅。
/// </summary>
public interface INotificationEffectOverlayService
{
    /// <summary>
    /// 当前平台是否支持覆盖层特效。
    /// </summary>
    bool IsSupported { get; }

    /// <summary>
    /// 预热覆盖层资源，避免首次播放时创建窗口造成卡顿。
    /// </summary>
    void Prewarm(int fpsLimit, NotificationEffectQuality quality);

    /// <summary>
    /// 播放一次涟漪特效。返回的任务在动画结束时完成。
    /// </summary>
    /// <param name="mainWindowHandle">用于定位特效中心的主窗口句柄。</param>
    /// <param name="color">涟漪颜色。</param>
    /// <param name="fpsLimit">帧率上限。</param>
    /// <param name="quality">动画精度。</param>
    Task PlayAsync(nint mainWindowHandle, Color color, int fpsLimit, NotificationEffectQuality quality);

    /// <summary>
    /// 释放覆盖层资源。
    /// </summary>
    void Shutdown();
}
