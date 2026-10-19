using Avalonia;
using Avalonia.Controls;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Assists;

namespace ClassIsland.Core.Behaviors;

/// <summary>
/// <see cref="Panel"/> 进入动画行为：把 <c>animated-intro</c> 面板的子元素交给
/// <see cref="IntroAnimationHelper"/> 播放错峰入场动画（仅在动画级别为“华丽”时生效）。
/// </summary>
public class StackPanelIntroAnimationBehavior
{
    public static readonly AttachedProperty<bool> IsIntroAnimationEnabledProperty =
        AvaloniaProperty.RegisterAttached<StackPanelIntroAnimationBehavior, Panel, bool>("IsIntroAnimationEnabled");

    public static void SetIsIntroAnimationEnabled(Panel obj, bool value) => obj.SetValue(IsIntroAnimationEnabledProperty, value);
    public static bool GetIsIntroAnimationEnabled(Panel obj) => obj.GetValue(IsIntroAnimationEnabledProperty);

    public static readonly AttachedProperty<bool> IsAnimationPlayingStartedProperty =
        AvaloniaProperty.RegisterAttached<StackPanelIntroAnimationBehavior, Panel, bool>("IsAnimationPlayingStarted");

    public static void SetIsAnimationPlayingStarted(Panel obj, bool value) => obj.SetValue(IsAnimationPlayingStartedProperty, value);
    public static bool GetIsAnimationPlayingStarted(Panel obj) => obj.GetValue(IsAnimationPlayingStartedProperty);

    static StackPanelIntroAnimationBehavior()
    {
        IsIntroAnimationEnabledProperty.Changed.AddClassHandler<Panel>(HandleIsIntroAnimationChanged);
    }

    private static void HandleIsIntroAnimationChanged(Panel panel, AvaloniaPropertyChangedEventArgs args)
    {
        if (!GetIsIntroAnimationEnabled(panel) || GetIsAnimationPlayingStarted(panel) ||
            IThemeService.AnimationLevel < 2)
        {
            return;
        }

        // 保持原有语义：只对没有自定义变换的子元素播放入场动画，避免覆盖子元素自身的 RenderTransform
        // （IntroAnimationHelper 会用 TranslateTransform 覆盖参与元素的变换）。
        var targets = panel.Children
            .Where(x => x.RenderTransform == null && x.IsVisible)
            .ToList();
        if (targets.Count == 0)
        {
            return;
        }

        SetIsAnimationPlayingStarted(panel, true);
        // 先写入隐藏态，保证可见首帧即为隐藏态，避免闪现最终态。
        IntroAnimationHelper.Prepare(targets);
        panel.Loaded += (_, _) => _ = IntroAnimationHelper.PlayAsync(targets);
    }
}
