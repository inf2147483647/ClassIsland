using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;

namespace ClassIsland.Core.Assists;

/// <summary>
/// 以附加属性 / 样式声明的方式启用“华丽动画”各项能力：鼠标滚轮平滑滚动、折叠栏平滑展开、标签页翻页过场。
/// </summary>
/// <remarks>
/// 能力本身由 <see cref="IntroAnimationHelper"/> 提供，且仅在动画级别为“华丽”时真正生效；
/// 其余档位下附加属性不会产生任何效果（属性值仍会保留，切换档位需要重启应用）。
/// <para>
/// 用法（样式或 XAML 直接声明）：
/// <code>
/// &lt;Style Selector=":is(ScrollViewer)"&gt;
///     &lt;Setter Property="assists:IntroAnimationAssist.IsSmoothScrollEnabled" Value="True"/&gt;
/// &lt;/Style&gt;
/// </code>
/// </para>
/// </remarks>
public class IntroAnimationAssist
{
    /// <summary>是否为该滚动容器启用鼠标滚轮平滑滚动。</summary>
    public static readonly AttachedProperty<bool> IsSmoothScrollEnabledProperty =
        AvaloniaProperty.RegisterAttached<IntroAnimationAssist, ScrollViewer, bool>("IsSmoothScrollEnabled");

    /// <summary>设置是否为该滚动容器启用鼠标滚轮平滑滚动。</summary>
    public static void SetIsSmoothScrollEnabled(ScrollViewer obj, bool value) => obj.SetValue(IsSmoothScrollEnabledProperty, value);

    /// <summary>获取是否为该滚动容器启用鼠标滚轮平滑滚动。</summary>
    public static bool GetIsSmoothScrollEnabled(ScrollViewer obj) => obj.GetValue(IsSmoothScrollEnabledProperty);

    /// <summary>是否把该控件包进平滑展开宿主（用于折叠栏的高度过渡）。</summary>
    public static readonly AttachedProperty<bool> IsExpandHostEnabledProperty =
        AvaloniaProperty.RegisterAttached<IntroAnimationAssist, Control, bool>("IsExpandHostEnabled");

    /// <summary>设置是否把该控件包进平滑展开宿主。</summary>
    public static void SetIsExpandHostEnabled(Control obj, bool value) => obj.SetValue(IsExpandHostEnabledProperty, value);

    /// <summary>获取是否把该控件包进平滑展开宿主。</summary>
    public static bool GetIsExpandHostEnabled(Control obj) => obj.GetValue(IsExpandHostEnabledProperty);

    /// <summary>是否为该标签页控件启用翻页过场动画。</summary>
    public static readonly AttachedProperty<bool> IsPageFlipEnabledProperty =
        AvaloniaProperty.RegisterAttached<IntroAnimationAssist, TabControl, bool>("IsPageFlipEnabled");

    /// <summary>设置是否为该标签页控件启用翻页过场动画。</summary>
    public static void SetIsPageFlipEnabled(TabControl obj, bool value) => obj.SetValue(IsPageFlipEnabledProperty, value);

    /// <summary>获取是否为该标签页控件启用翻页过场动画。</summary>
    public static bool GetIsPageFlipEnabled(TabControl obj) => obj.GetValue(IsPageFlipEnabledProperty);

    private static readonly ConditionalWeakTable<ScrollViewer, object> SmoothedScrollViewers = new();

    private static readonly ConditionalWeakTable<Control, object> ExpandHostedControls = new();

    static IntroAnimationAssist()
    {
        IsSmoothScrollEnabledProperty.Changed.AddClassHandler<ScrollViewer>(OnSmoothScrollChanged);
        IsExpandHostEnabledProperty.Changed.AddClassHandler<Control>(OnExpandHostChanged);
        IsPageFlipEnabledProperty.Changed.AddClassHandler<TabControl>(OnPageFlipChanged);
    }

    private static void OnSmoothScrollChanged(ScrollViewer scrollViewer, AvaloniaPropertyChangedEventArgs args)
    {
        if (!GetIsSmoothScrollEnabled(scrollViewer) || SmoothedScrollViewers.TryGetValue(scrollViewer, out _))
        {
            return;
        }

        IntroAnimationHelper.EnableScrollViewerSmoothing(scrollViewer);
        SmoothedScrollViewers.Add(scrollViewer, new object());

        // 离开视觉树时撤销，避免静态表里堆积已经销毁的滚动容器。
        void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
        {
            scrollViewer.DetachedFromVisualTree -= OnDetached;
            IntroAnimationHelper.DisableScrollViewerSmoothing(scrollViewer);
            SmoothedScrollViewers.Remove(scrollViewer);
        }

        scrollViewer.DetachedFromVisualTree += OnDetached;
    }

    private static void OnExpandHostChanged(Control control, AvaloniaPropertyChangedEventArgs args)
    {
        if (!GetIsExpandHostEnabled(control))
        {
            return;
        }

        // 已挂到视觉树上就直接替换，否则等挂上之后再做（此时父级已确定，且早于首次布局）。
        if (TopLevel.GetTopLevel(control) != null)
        {
            WrapInExpandHost(control);
            return;
        }

        void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
        {
            control.AttachedToVisualTree -= OnAttached;
            WrapInExpandHost(control);
        }

        control.AttachedToVisualTree += OnAttached;
    }

    private static void WrapInExpandHost(Control control)
    {
        if (!IntroAnimationHelper.IsEnabled || ExpandHostedControls.TryGetValue(control, out _))
        {
            return;
        }

        // 只有直接位于 Panel 子项中的控件才能就地替换；模板 / 条目容器内的父级不是 Panel，跳过。
        if (control.Parent is not Panel panel)
        {
            return;
        }

        var index = panel.Children.IndexOf(control);
        if (index < 0)
        {
            return;
        }

        // 先摘除再交给宿主，避免“控件已有父级”异常。
        panel.Children.RemoveAt(index);
        panel.Children.Insert(index, IntroAnimationHelper.CreateExpandHost(control));
        ExpandHostedControls.Add(control, new object());
    }

    private static void OnPageFlipChanged(TabControl tabControl, AvaloniaPropertyChangedEventArgs args)
    {
        if (GetIsPageFlipEnabled(tabControl))
        {
            IntroAnimationHelper.EnablePageFlip(tabControl);
        }
    }
}
