using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Shared;
using FluentAvalonia.UI.Controls;
using Microsoft.Extensions.Logging;

namespace ClassIsland.Core.Assists;

/// <summary>
/// “华丽动画”工具集（Panel 子元素错峰入场 / 出场 / 横向滑动、鼠标滚轮平滑滚动，
/// 以及折叠栏的平滑展开 / 折叠宿主）。
/// <para>
/// 参数沿用 <c>StackPanelIntroAnimation.axaml</c>（错峰间隔 25ms、单项 750ms、
/// 缓动 0,1,0,1、位移 50px）；实现为<b>纯代码本地值 + 手动插值时钟</b>：先把子元素写入隐藏态本地值，
/// 再用计时器逐帧插值写回，不依赖 Avalonia 样式动画系统，
/// 因此不存在“填充态清除延迟”导致的“先显示最终态再重播”抽动。
/// </para>
/// <para>
/// 仅在动画级别为“华丽”（<see cref="IThemeService.AnimationLevel"/> &gt;= 2）时播放动画；
/// 其余档位下所有方法（含平滑滚动、折叠栏过渡）退化为“直接置为最终可见态”/放行原生行为。
/// 同一时刻只允许一组错峰动画在跑，新调用会取代旧动画
/// （被取代时，前一组元素会被直接落到最终态，不会卡在半透明中途）。
/// </para>
/// <para>
/// 用法：页面变为可见<b>之前</b>调用 <see cref="Prepare"/> 让首帧即为隐藏态（避免闪现），
/// 页面可见时调用 <see cref="PlayAsync(Panel)"/> / <see cref="PlayExitAsync(Panel)"/>；
/// 页面构造函数中调用 <see cref="EnableSmoothScroll"/> 接入滚轮平滑滚动；
/// 设置分组折叠栏用 <see cref="CreateExpandHost"/> 包一层以获得平滑高度过渡。
/// 整面板重载会自动排除自带 <see cref="RenderTransform"/> 的子元素。
/// </para>
/// </summary>
public static class IntroAnimationHelper
{
    private const double HiddenY = -25.0;
    private const double StartY = 50.0;        // 入场起点：位于下方 50px，向上滑入
    private const double ExitY = -50.0;        // 出场终点：继续向上滑出 50px（与入场同方向）
    private const double SlideDistance = 50.0; // 水平入场/出场位移（与纵向 50px 量级一致）

    private static readonly TimeSpan StepInterval = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(750);
    private static readonly IEasing IntroEasing = Easing.Parse("0.00,1.00,0.00,1.00");

    // 出场专用节奏：入场缓动 0,1,0,1 在 750ms 上的等效淡入时间约 150ms（x=0.2 时 y≈0.93），
    // 出场若沿用 750ms 会出现“已经看不见却仍在等待”的空档。故出场时长直接对齐到 150ms，
    // 线性缓动让位移与透明度在整段内均匀走完、结束时恰好到位，不吃尾巴。
    private static readonly TimeSpan ExitDuration = TimeSpan.FromMilliseconds(150);
    private static readonly IEasing ExitEasing = new LinearEasing();

    // 当前动画节奏（同一时刻只有一个动画在跑：RunAsync 写入、OnTick 读取）
    private static TimeSpan _activeDuration = Duration;
    private static IEasing _activeEasing = IntroEasing;

    private sealed class Entry
    {
        public required Control Control;
        public required TranslateTransform Transform;
        public long StartTimestamp;
        public double FromX;
        public double ToX;
        public double FromY;
        public double ToY;
        public double FromOpacity;
        public double ToOpacity;
    }

    private static readonly List<Entry> Entries = new();
    private static DispatcherTimer? _timer;
    private static TaskCompletionSource? _completion;

    /// <summary>当前动画级别是否允许播放“华丽动画”。</summary>
    public static bool IsEnabled => IThemeService.AnimationLevel >= 2;

    /// <summary>诊断日志。宿主尚未就绪时静默返回 <c>null</c>。</summary>
    private static ILogger? Logger
    {
        get
        {
            try
            {
                // 通过 ILoggerFactory 取日志：容器只注册了 ILoggerFactory 与 ILogger&lt;T&gt;，没有非泛型 ILogger。
                return IAppHost.TryGetService<ILoggerFactory>()?.CreateLogger("ClassIsland.IntroAnimation");
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// 面板错峰动画的参与元素：可见的直接子元素。
    /// <para>
    /// 注意：本工具会用 <see cref="TranslateTransform"/> 覆盖子元素自身的 <see cref="RenderTransform"/>，
    /// 因此整面板重载只应用于没有自定义变换的普通容器（设置页的根 StackPanel 即属此类）。
    /// </para>
    /// </summary>
    private static IEnumerable<Control> GetTargets(Panel panel)
        => panel.Children.Where(x => x.IsVisible);

    /// <summary>
    /// 把面板内所有可见子元素立即写入“未播放”的隐藏态（Opacity=0、Y=-25）。
    /// 应在页面变为可见之前调用，保证内容可见首帧即为隐藏态。幂等。
    /// </summary>
    public static void Prepare(Panel panel)
        => Prepare(GetTargets(panel));

    /// <summary>
    /// 把指定的这组控件立即写入“未播放”的隐藏态（Opacity=0、Y=-25）。
    /// 用于调用方已自行筛选参与元素（例如需要排除自带 <see cref="RenderTransform"/> 的子元素）的场景。
    /// </summary>
    public static void Prepare(IEnumerable<Control> targets)
    {
        if (!IsEnabled)
        {
            return;
        }

        foreach (var c in targets)
        {
            c.Opacity = 0;
            c.RenderTransform = new TranslateTransform { Y = HiddenY };
        }
    }

    /// <summary>
    /// 播放面板的错峰入场动画：先把子元素写入隐藏态，再立即播放。
    /// 页面加载时调用（建议放在 <c>Loaded</c> 处理器最前面，避免后续初始化逻辑异常时中断）。
    /// </summary>
    public static void PlayIntro(Panel panel)
    {
        if (!IsEnabled)
        {
            return;
        }

        var targets = GetTargets(panel).ToList();
        if (targets.Count == 0)
        {
            return;
        }

        // 先写入隐藏态，保证可见首帧即为隐藏态（避免闪现最终态）
        foreach (var c in targets)
        {
            c.Opacity = 0;
            c.RenderTransform = new TranslateTransform { Y = HiddenY };
        }

        Logger?.LogInformation(
            "[IntroAnimation] 播放入场动画：目标 {Count} 个，动画级别 {Level}，面板可见 {Visible}",
            targets.Count, IThemeService.AnimationLevel, panel.IsVisible);

        _ = PlayAsync(targets);
    }

    /// <summary>停止并清空进行中的错峰动画（等待中的调用方会立即完成，避免永久挂起）。</summary>
    public static void Stop()
    {
        _timer?.Stop();
        _timer = null;

        // 被取代 / 中止时，把进行中的元素直接落到最终态，
        // 否则前一组元素会永久停在半透明或位移中途（多面板连续播放入场动画时可见）。
        foreach (var entry in Entries)
        {
            entry.Control.Opacity = entry.ToOpacity;
            entry.Transform.X = entry.ToX;
            entry.Transform.Y = entry.ToY;
        }

        Entries.Clear();
        _completion?.TrySetResult();
        _completion = null;
    }

    /// <summary>以 25ms 间隔逐个触发子元素入场动画（自下而上滑入）。不等待动画结束。</summary>
    public static void Play(Panel panel) => _ = PlayAsync(panel);

    /// <summary>入场动画（自下而上，Y 50 → 0 + 淡入），可等待播放完毕。</summary>
    public static Task PlayAsync(Panel panel) => PlayAsync(GetTargets(panel));

    /// <summary>
    /// 入场动画，但只作用于指定的这组控件（用于页面中“局部参与动画”的场景）。
    /// </summary>
    public static Task PlayAsync(IEnumerable<Control> targets)
        => RunAsync(targets,
            0.0, 0.0, StartY, 0.0,
            0.0, 1.0, Duration, StepInterval, IntroEasing);

    /// <summary>
    /// 水平错峰入场：从右侧（X 50 → 0）或左侧（X -50 → 0）逐个滑入并淡入，
    /// 位移量、淡入时长、缓动与错峰间隔均与“向上入场”一致。
    /// </summary>
    public static Task PlaySlideInAsync(IEnumerable<Control> targets, bool fromRight)
        => RunAsync(targets,
            fromRight ? SlideDistance : -SlideDistance, 0.0, 0.0, 0.0,
            0.0, 1.0, Duration, StepInterval, IntroEasing);

    /// <summary>
    /// 出场动画：与入场相同的错峰顺序、间隔与运动方向（向上），子元素从当前位置继续
    /// 向上滑出（Y 0 → -50）并淡出。结束后元素停在隐藏态。
    /// </summary>
    public static Task PlayExitAsync(Panel panel) => PlayExitAsync(GetTargets(panel));

    /// <summary>出场动画，但只作用于指定的这组控件。</summary>
    public static Task PlayExitAsync(IEnumerable<Control> targets)
        => RunAsync(targets,
            0.0, 0.0, 0.0, ExitY,
            1.0, 0.0, ExitDuration, StepInterval, ExitEasing);

    /// <summary>
    /// 水平整体出场：所有目标在同一时刻向左（X 0 → -50）或向右（X 0 → 50）滑出并淡出，
    /// 无错峰。
    /// </summary>
    public static Task PlaySlideOutAsync(IEnumerable<Control> targets, bool toLeft)
        => RunAsync(targets,
            0.0, toLeft ? -SlideDistance : SlideDistance, 0.0, 0.0,
            1.0, 0.0, ExitDuration, TimeSpan.Zero, ExitEasing);

    /// <summary>
    /// 错峰动画通用实现：调用前先复位到 fromX/fromY/fromOpacity，再逐帧插值到 toX/toY/toOpacity。
    /// stepInterval 为 0 时所有目标同时起步；否则按索引错峰。
    /// </summary>
    private static Task RunAsync(IEnumerable<Control> targets,
        double fromX, double toX, double fromY, double toY,
        double fromOpacity, double toOpacity,
        TimeSpan duration, TimeSpan stepInterval, IEasing easing)
    {
        // 取代上一次动画，避免旧时钟干扰新页面
        Stop();

        var list = targets.Where(x => x.IsVisible).ToList();
        if (list.Count == 0)
        {
            return Task.CompletedTask;
        }

        // 宿主禁用动画：直接置为最终态，不做插值
        if (!IsEnabled)
        {
            foreach (var c in list)
            {
                c.Opacity = toOpacity;
                c.RenderTransform = new TranslateTransform { X = toX, Y = toY };
            }
            return Task.CompletedTask;
        }

        _activeDuration = duration;
        _activeEasing = easing;

        var now = Stopwatch.GetTimestamp();
        for (var i = 0; i < list.Count; i++)
        {
            var c = list[i];
            // 动画起点（页面若先前已 Prepare，此处首帧即被覆盖，不会闪现）
            var transform = new TranslateTransform { X = fromX, Y = fromY };
            c.RenderTransform = transform;
            c.Opacity = fromOpacity;

            Entries.Add(new Entry
            {
                Control = c,
                Transform = transform,
                StartTimestamp = now + stepInterval.Ticks * Stopwatch.Frequency / TimeSpan.TicksPerSecond * i,
                FromX = fromX,
                ToX = toX,
                FromY = fromY,
                ToY = toY,
                FromOpacity = fromOpacity,
                ToOpacity = toOpacity
            });
        }

        var completion = new TaskCompletionSource();
        _completion = completion;

        _timer = new DispatcherTimer(DispatcherPriority.Send)
        {
            Interval = TimeSpan.FromMilliseconds(10)
        };
        _timer.Tick += OnTick;
        _timer.Start();

        return completion.Task;
    }

    private static void OnTick(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        var finished = true;

        foreach (var entry in Entries)
        {
            if (now < entry.StartTimestamp)
            {
                finished = false;
                continue;
            }

            var elapsed = (now - entry.StartTimestamp) / (double)Stopwatch.Frequency;
            var progress = elapsed / _activeDuration.TotalSeconds;
            if (progress >= 1.0)
            {
                progress = 1.0;
            }
            else
            {
                finished = false;
            }

            var eased = _activeEasing.Ease(progress);
            entry.Control.Opacity = entry.FromOpacity + (entry.ToOpacity - entry.FromOpacity) * eased;
            entry.Transform.X = entry.FromX + (entry.ToX - entry.FromX) * eased;
            entry.Transform.Y = entry.FromY + (entry.ToY - entry.FromY) * eased;
        }

        if (finished)
        {
            Stop();
        }
    }

    // ==================== 鼠标滚轮平滑滚动 ====================
    // 与上方错峰动画同属本“华丽动画”工具集，共用同一开关（动画级别非禁用时生效）。
    // 做法：在 ScrollViewer 上挂隧道阶段的 PointerWheelChanged，抢在其自带的（冒泡）滚动之前接管；
    // 每格滚轮累加“目标偏移”，再用 ~60fps 计时器让实际偏移以指数缓出逼近目标——
    // 连续滚轮时目标持续累加，因此不会出现反复重启动画造成的跳动。
    // 到上下边界或无滚动空间时放行原生行为（保留滚动链）；嵌套滚动只在最内层可滚动者生效。

    /// <summary>一次滚轮格滚动多少行（贴近系统默认的“一格滚轮 = 3 行”）。</summary>
    private const double LinesPerNotch = 3.0;

    /// <summary>行高兜底值（<see cref="ScrollViewer.SmallChange"/> 尚未就绪时使用）。</summary>
    private const double FallbackLineHeight = 16.0;

    /// <summary>滚轮平滑滚动每帧向目标偏移逼近的比例（指数缓出，约 120ms 收敛）。</summary>
    private const double ScrollLerpFactor = 0.28;

    /// <summary>滚轮平滑滚动小于该距离时直接吸附到目标并停止计时器。</summary>
    private const double ScrollSnapEpsilon = 0.5;

    private static readonly TimeSpan ScrollFrameInterval = TimeSpan.FromMilliseconds(16);

    /// <summary>已接入平滑滚动的 ScrollViewer → 状态。</summary>
    private static readonly Dictionary<ScrollViewer, ScrollState> ScrollStates = new();

    /// <summary>已接入平滑滚动的 ScrollViewer → 引用计数（宿主外层 ScrollViewer 会被多个页面共用）。</summary>
    private static readonly Dictionary<ScrollViewer, int> ScrollRefCounts = new();

    /// <summary>页面 → 由该页面接入平滑滚动的 ScrollViewer（卸载时逐一撤销）。</summary>
    private static readonly ConditionalWeakTable<Control, List<ScrollViewer>> PageScrollViewers = new();

    /// <summary>
    /// 为一整个页面接入鼠标滚轮平滑滚动：页面加载后自动接管其内部的 ScrollViewer，
    /// 以及宿主为页面提供的外层 ScrollViewer（部分页面自身不含滚动容器）；
    /// 页面卸载时撤销，避免影响随后显示的其它页面 / 宿主界面。
    /// </summary>
    public static void EnableSmoothScroll(Control page)
    {
        page.Loaded += OnSmoothScrollPageLoaded;
        page.Unloaded += OnSmoothScrollPageUnloaded;
    }

    /// <summary>立即结束某个 ScrollViewer 正在进行的平滑滚动（不改变当前偏移）。</summary>
    public static void StopSmoothScroll(ScrollViewer scrollViewer)
    {
        if (ScrollStates.TryGetValue(scrollViewer, out var state))
        {
            state.StopTimer();
        }
    }

    /// <summary>
    /// 为单个 <see cref="ScrollViewer"/> 接入鼠标滚轮平滑滚动。
    /// 供附加属性 / 样式声明式启用（见 <c>IntroAnimationAssist.IsSmoothScrollEnabled</c>）使用。
    /// </summary>
    public static void EnableScrollViewerSmoothing(ScrollViewer scrollViewer)
    {
        if (!IsEnabled)
        {
            return;
        }

        AttachSmoothScroll(scrollViewer);
    }

    /// <summary>撤销单个 <see cref="ScrollViewer"/> 的鼠标滚轮平滑滚动。</summary>
    public static void DisableScrollViewerSmoothing(ScrollViewer scrollViewer)
    {
        DetachSmoothScroll(scrollViewer);
    }

    private static void AttachSmoothScroll(ScrollViewer scrollViewer)
    {
        // 引用计数：宿主外层 ScrollViewer 会被多个页面（及其嵌套页）共用，
        // 只有首个持有者才真正挂事件，其余仅计数，避免页面切换时被后来者重复挂载 / 提前摘除。
        ScrollRefCounts.TryGetValue(scrollViewer, out var count);
        ScrollRefCounts[scrollViewer] = count + 1;
        if (count > 0)
        {
            return;
        }

        var state = new ScrollState(scrollViewer);
        ScrollStates.Add(scrollViewer, state);
        state.Attach();
    }

    private static void DetachSmoothScroll(ScrollViewer scrollViewer)
    {
        if (!ScrollRefCounts.TryGetValue(scrollViewer, out var count))
        {
            return;
        }

        if (count > 1)
        {
            ScrollRefCounts[scrollViewer] = count - 1;
            return;
        }

        ScrollRefCounts.Remove(scrollViewer);
        if (ScrollStates.Remove(scrollViewer, out var state))
        {
            state.Detach();
        }
    }

    private static void OnSmoothScrollPageLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control page)
        {
            return;
        }

        var list = PageScrollViewers.GetOrCreateValue(page);

        foreach (var scrollViewer in page.GetVisualDescendants().OfType<ScrollViewer>())
        {
            AttachSmoothScroll(scrollViewer);
            if (!list.Contains(scrollViewer))
            {
                list.Add(scrollViewer);
            }
        }

        // 页面自身不含滚动容器时，滚动由宿主提供的外层 ScrollViewer 承担
        var outer = page.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
        if (outer != null)
        {
            AttachSmoothScroll(outer);
            if (!list.Contains(outer))
            {
                list.Add(outer);
            }
        }
    }

    private static void OnSmoothScrollPageUnloaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control page || !PageScrollViewers.TryGetValue(page, out var list))
        {
            return;
        }

        foreach (var scrollViewer in list)
        {
            DetachSmoothScroll(scrollViewer);
        }
        list.Clear();
    }

    /// <summary>单个 ScrollViewer 的滚轮平滑滚动状态与插值时钟。</summary>
    private sealed class ScrollState
    {
        private readonly ScrollViewer _scrollViewer;
        private readonly EventHandler<PointerWheelEventArgs> _wheelHandler;
        private readonly EventHandler<PointerPressedEventArgs> _pressedHandler;

        private DispatcherTimer? _timer;
        private double _targetY;

        public ScrollState(ScrollViewer scrollViewer)
        {
            _scrollViewer = scrollViewer;
            _wheelHandler = OnWheel;
            _pressedHandler = OnPressed;
        }

        public void Attach()
        {
            // 隧道阶段先于 ScrollViewer 自带的（冒泡）滚轮处理，才能拦下原生跳变
            _scrollViewer.AddHandler(InputElement.PointerWheelChangedEvent, _wheelHandler, RoutingStrategies.Tunnel);
            // 用户按下（拖动滚动条 / 点内容）时立即交回滚动控制权
            _scrollViewer.AddHandler(InputElement.PointerPressedEvent, _pressedHandler, RoutingStrategies.Tunnel, handledEventsToo: true);
        }

        public void Detach()
        {
            StopTimer();
            _scrollViewer.RemoveHandler(InputElement.PointerWheelChangedEvent, _wheelHandler);
            _scrollViewer.RemoveHandler(InputElement.PointerPressedEvent, _pressedHandler);
        }

        public void StopTimer()
        {
            if (_timer == null)
            {
                return;
            }

            _timer.Stop();
            _timer.Tick -= OnTick;
            _timer = null;
        }

        private void OnPressed(object? sender, PointerPressedEventArgs e) => StopTimer();

        private void OnWheel(object? sender, PointerWheelEventArgs e)
        {
            if (e.Handled || !IsEnabled)
            {
                return;
            }

            // 仅处理纵向滚轮；横向（或纯触控板横向）放行原生行为
            if (Math.Abs(e.Delta.Y) < 1e-4)
            {
                return;
            }

            // 嵌套滚动：只在“光标下最近的可纵向滚动容器”上接管（内层不可滚动的 ScrollViewer 会被跳过）
            if (!IsNearestScrollableScrollViewer(e.Source))
            {
                return;
            }

            var maximum = _scrollViewer.ScrollBarMaximum.Y;
            if (maximum <= 0.01)
            {
                return;
            }

            var lineHeight = _scrollViewer.SmallChange.Height;
            if (double.IsNaN(lineHeight) || lineHeight <= 0.01)
            {
                lineHeight = FallbackLineHeight;
            }

            // 未在动画中时以当前实际偏移为基准（兼容用户刚拖动过滚动条）
            if (_timer == null)
            {
                _targetY = _scrollViewer.Offset.Y;
            }

            var step = lineHeight * LinesPerNotch;
            var newTarget = Math.Clamp(_targetY - e.Delta.Y * step, 0, maximum);

            // 已到边界且无变化：放行原生行为（保留滚动链）
            if (Math.Abs(newTarget - _targetY) < 0.01)
            {
                return;
            }

            _targetY = newTarget;
            e.Handled = true;
            EnsureTimer();
        }

        private void EnsureTimer()
        {
            if (_timer != null)
            {
                return;
            }

            _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = ScrollFrameInterval };
            _timer.Tick += OnTick;
            _timer.Start();
        }

        private void OnTick(object? sender, EventArgs e)
        {
            var current = _scrollViewer.Offset.Y;
            var diff = _targetY - current;

            if (Math.Abs(diff) <= ScrollSnapEpsilon)
            {
                _scrollViewer.Offset = new Vector(_scrollViewer.Offset.X, _targetY);
                StopTimer();
                return;
            }

            _scrollViewer.Offset = new Vector(_scrollViewer.Offset.X, current + diff * ScrollLerpFactor);
        }

        /// <summary>
        /// 光标下本 ScrollViewer 是否为“最近的可纵向滚动容器”。
        /// <para>
        /// 介于事件源与本容器之间的更内层 ScrollViewer，若当前实际不可纵向滚动（<see cref="ScrollViewer.ScrollBarMaximum"/>
        /// 无剩余空间——典型场景：宿主用无限高度测量页面，内层 ScrollViewer 永远不溢出）就跳过它继续向外找；
        /// 否则把滚轮交给这个不可滚动的内层，会表现为“页面完全不响应滚轮”。
        /// </para>
        /// </summary>
        private bool IsNearestScrollableScrollViewer(object? source)
        {
            if (ReferenceEquals(source, _scrollViewer))
            {
                return true;
            }

            if (source is not Visual visual)
            {
                return true;
            }

            foreach (var ancestor in visual.GetVisualAncestors())
            {
                if (ReferenceEquals(ancestor, _scrollViewer))
                {
                    return true;
                }

                // 更内层存在“确实可滚动”的 ScrollViewer：本次滚轮归它，本容器放行
                if (ancestor is ScrollViewer nearer && nearer.ScrollBarMaximum.Y > 0.01)
                {
                    return false;
                }
            }

            // 不在本 ScrollViewer 内（理论上不会出现），保守放行
            return false;
        }
    }

    // ==================== 标签页翻页过场（导航栏切换） ====================
    // 与上方动画同属本“华丽动画”工具集，共用同一开关（动画级别非禁用时生效）。
    // 效果对齐 ClassIsland「档案编辑」导航栏切换的翻页动画（旧页滑出、新页自另一侧滑入并淡入）。

    /// <summary>入页时长与缓动（与 ClassIsland 翻页一致：250ms、KeySpline(0,0)(0,1)）。</summary>
    private static readonly TimeSpan PageFlipInDuration = TimeSpan.FromMilliseconds(250);

    /// <summary>旧页滑出时长：比入页短，避免“切换前先等旧页滑完”造成明显迟滞。</summary>
    private static readonly TimeSpan PageFlipOutDuration = TimeSpan.FromMilliseconds(120);

    private static readonly Easing PageFlipEasing = Easing.Parse("0,0,0,1");

    /// <summary>标签页内容 → 翻页动画状态（原始 RenderTransform / 正在播放的计时器）。</summary>
    private static readonly ConditionalWeakTable<Control, PageFlipState> PageFlipStates = new();

    /// <summary>旧页滑出期间是否已排队一次切换：期间再次点击不再拦截，交由框架立即切换。</summary>
    private static bool _pageFlipSwitching;

    /// <summary>
    /// 给标签页控件套用「翻页」过场动画：切换标签页时旧页滑出淡出、新页自右侧滑入淡入
    /// （时长 / 缓动对齐 ClassIsland 翻页动画）。
    /// <para>
    /// 这里**不使用**框架的 <c>TabControl.PageTransition</c>：它会用同一批评为演示器承载各标签页，
    /// 而本插件的标签页内容是选中后才懒加载的（过场开始时新页内容还是 null，随后才赋值），
    /// 演示器在过场中途被换填会让过场"卡在一半"、整页空白。改为自己播：点击标签头时先播旧页滑出，
    /// 播毕再真正切换（切换后播新页滑入）；每段动画结束时都精确落到终态并还原原始 RenderTransform，
    /// 不会留下半透明 / 偏移。
    /// </para>
    /// </summary>
    /// <remarks>
    /// ClassIsland 当前未接入本方法：标签页过场统一由 <c>StylesBase.axaml</c> 中
    /// <see cref="TabControl"/> 的 <c>CompositionPageTransition</c> 承担，
    /// 两者同时生效会互相干扰。本方法保留，供需要自行接管过场的场景使用。
    /// </remarks>
    public static void EnablePageFlip(TabControl tabControl)
    {
        // 关掉框架 / 主题自带的过场动画（Avalonia 11 无此属性，反射设置）
        tabControl.GetType()
            .GetProperty("PageTransition", BindingFlags.Public | BindingFlags.Instance)
            ?.SetValue(tabControl, null);

        // 隧道路由：抢在 TabItem 自身的选中处理之前，才能“延迟切换、先播旧页滑出”
        tabControl.AddHandler(InputElement.PointerPressedEvent, OnTabPointerPressed, RoutingStrategies.Tunnel);
        tabControl.SelectionChanged += (_, e) => OnTabSelectionChanged(tabControl, e);
    }

    /// <summary>
    /// 拦下“点击另一个标签头”的按下事件：先播旧页滑出，播毕再真正切换（切换后由
    /// <see cref="OnTabSelectionChanged"/> 播新页滑入）。命中即标记已处理，阻止 TabItem 立刻切换。
    /// 取不到旧页内容 / 宽度时放行，交给框架立即切换（不拦、无出场动画）。
    /// </summary>
    private static void OnTabPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!IsEnabled || _pageFlipSwitching || sender is not TabControl tabControl)
        {
            return;
        }

        if (!e.GetCurrentPoint(tabControl).Properties.IsLeftButtonPressed)
        {
            return;
        }

        for (Visual? visual = e.Source as Visual; visual != null; visual = visual.GetVisualParent())
        {
            if (visual is not TabItem target)
            {
                continue;
            }

            if (ReferenceEquals(target, tabControl.SelectedItem) ||
                (tabControl.SelectedItem as TabItem)?.Content is not Control outgoing)
            {
                return;
            }

            var distance = GetPageFlipDistance(tabControl, outgoing);
            if (distance <= 0)
            {
                return;
            }

            // 拦住本次点击，先播旧页滑出，播毕再切换（方向按标签页次序：向右切换旧页向左滑出，反之镜像）
            var forward = IsForwardSwitch(tabControl, tabControl.SelectedItem, target);
            e.Handled = true;
            _pageFlipSwitching = true;
            AnimatePageContent(outgoing, 0, forward ? -distance : distance, 1, 0, PageFlipOutDuration, () =>
            {
                _pageFlipSwitching = false;
                tabControl.SelectedItem = target;
            });
            return;
        }
    }

    private static void OnTabSelectionChanged(TabControl tabControl, SelectionChangedEventArgs e)
    {
        if (!IsEnabled || e.AddedItems.Count == 0 || e.AddedItems[0] is not TabItem tabItem)
        {
            return;
        }

        // 方向：目标页在原页右侧则新页自右侧滑入，否则自左侧滑入
        var forward = IsForwardSwitch(
            tabControl,
            e.RemovedItems.Count > 0 ? e.RemovedItems[0] : null,
            tabItem);

        if (tabItem.Content is Control)
        {
            PlayPageFlipIn(tabControl, tabItem, forward);
            return;
        }

        // 懒加载：内容要等调用方的 SelectionChanged 处理完才创建，赋值后再补播
        void OnContentChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
        {
            if (args.Property != ContentControl.ContentProperty)
            {
                return;
            }

            tabItem.PropertyChanged -= OnContentChanged;
            if (ReferenceEquals(tabControl.SelectedItem, tabItem))
            {
                PlayPageFlipIn(tabControl, tabItem, forward);
            }
        }

        tabItem.PropertyChanged += OnContentChanged;
    }

    /// <summary>
    /// 判断切换方向：目标页在来源页右侧返回 true（旧页向左滑出、新页自右侧滑入），
    /// 在左侧则返回 false（镜像）。取不到序号时按 true 处理。
    /// </summary>
    private static bool IsForwardSwitch(TabControl tabControl, object? from, object? to)
    {
        var fromIndex = from == null ? -1 : tabControl.Items.IndexOf(from);
        var toIndex = to == null ? -1 : tabControl.Items.IndexOf(to);
        return fromIndex < 0 || toIndex < 0 || toIndex >= fromIndex;
    }

    /// <summary>新页滑入 + 淡入（<paramref name="forward"/> 决定自右还是自左）。</summary>
    private static void PlayPageFlipIn(TabControl tabControl, TabItem tabItem, bool forward)
    {
        if (tabItem.Content is not Control content)
        {
            return;
        }

        var distance = GetPageFlipDistance(tabControl, content);
        if (distance <= 0)
        {
            return;
        }

        AnimatePageContent(content, forward ? distance : -distance, 0, 0, 1, PageFlipInDuration);
    }

    /// <summary>滑动距离取内容自身宽度；尚未布局时退回标签页控件的宽度。</summary>
    private static double GetPageFlipDistance(TabControl tabControl, Control content)
    {
        var distance = content.Bounds.Width > 0 ? content.Bounds.Width : tabControl.Bounds.Width;
        return double.IsNaN(distance) || distance <= 0 ? 0 : distance;
    }

    /// <summary>
    /// 播放一段页面滑动动画（位移为相对原位的偏移）：结束时精确落到终态并还原原始 RenderTransform。
    /// 起点取当前状态，因此被新的切换打断时可从当前位置续播。
    /// </summary>
    private static void AnimatePageContent(
        Control content,
        double fromOffset,
        double toOffset,
        double fromOpacity,
        double toOpacity,
        TimeSpan duration,
        Action? onCompleted = null)
    {
        var state = PageFlipStates.GetOrCreateValue(content);
        if (!state.Active)
        {
            // 首次接管：记录原始 RenderTransform，动画结束时还原（原位移不参与计算，避免叠加）
            state.PreviousTransform = content.RenderTransform;
            state.Active = true;
        }

        state.Timer?.Stop();

        // 用 TransformGroup 叠一个平移，不破坏内容自身已有的 RenderTransform
        var previous = state.PreviousTransform;
        var translate = new TranslateTransform { X = fromOffset };
        if (previous is Transform existingTransform)
        {
            var group = new TransformGroup();
            group.Children.Add(existingTransform);
            group.Children.Add(translate);
            content.RenderTransform = group;
        }
        else
        {
            content.RenderTransform = translate;
        }

        content.Opacity = fromOpacity;

        var durationMs = Math.Max(1, duration.TotalMilliseconds);
        var startedAt = Stopwatch.GetTimestamp();

        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = ScrollFrameInterval };
        state.Timer = timer;
        timer.Tick += (_, _) =>
        {
            var elapsedMs = (Stopwatch.GetTimestamp() - startedAt) * 1000.0 / Stopwatch.Frequency;
            var progress = Math.Clamp(elapsedMs / durationMs, 0.0, 1.0);
            var eased = PageFlipEasing.Ease(progress);

            translate.X = fromOffset + (toOffset - fromOffset) * eased;
            content.Opacity = fromOpacity + (toOpacity - fromOpacity) * eased;

            if (progress < 1.0)
            {
                return;
            }

            // 结束：还原原始视觉状态（旧页随后被切走不可见；新页恰好落在终态）
            content.RenderTransform = previous;
            content.Opacity = 1;
            state.Timer = null;
            state.Active = false;
            timer.Stop();
            onCompleted?.Invoke();
        };

        timer.Start();
    }

    private sealed class PageFlipState
    {
        public DispatcherTimer? Timer;
        public ITransform? PreviousTransform;
        public bool Active;
    }

    // ==================== 折叠栏平滑展开 / 折叠宿主 ====================
    // 与上方动画同属本“华丽动画”工具集，共用同一开关（动画级别非禁用时生效）。
    // 把带 IsExpanded 的控件（如 FA 的 SettingsExpander）包进一个裁剪 Border，
    // 展开 / 折叠时对 Border 高度做过渡，让后续元素被平滑推下 / 上移，而不是瞬间弹跳。

    // 时长与 ClassIsland 的 Expander 一致的展开时长（收起与之保持一致）；
    // 缓动沿用 WinUI：展开 KeySpline(0,0,0,1)、收起 KeySpline(1,1,0,1)。
    private static readonly TimeSpan ExpandSizeDuration = TimeSpan.FromMilliseconds(333);
    private static readonly TimeSpan CollapseSizeDuration = ExpandSizeDuration;
    private static readonly Easing ExpandSizeEasing = Easing.Parse("0,0,0,1");
    private static readonly Easing CollapseSizeEasing = Easing.Parse("1,1,0,1");

    /// <summary>
    /// 创建“平滑展开 / 折叠宿主”：把任意带 <c>IsExpanded</c> 属性的控件（如 FluentAvalonia 的
    /// SettingsExpander）包进一个裁剪容器，展开 / 折叠时对容器高度做过渡。
    /// 返回的 <see cref="Border"/> 直接作为子控件或模板根使用。
    /// </summary>
    /// <remarks>
    /// 展开 / 折叠动画完全由本宿主接管：创建后禁用被包裹控件内部的展开动画
    /// （FA3 的 WinUI 滑动 + Expander 内容交叉淡化），避免两套动画同时运行 —— 这是高频点击时“抽动”的根因。
    /// 被包裹控件在 <c>IsExpanded</c> 变为 false 的瞬间就会隐藏内容，若此时直接收缩会得到
    /// “内容瞬间消失、只剩空白在收缩”的效果；因此收起过程中把内部 <c>IsExpanded</c> 暂时引脚为展开
    /// （内容保持挂载、由宿主裁剪），待高度过渡结束后再真正折叠，与展开时逐段露出保持对称。
    /// 动画级别为“禁用”或无子条目（FA 会强制把 <c>IsExpanded</c> 夹回 false）时退化为普通包装容器，不干预尺寸。
    /// </remarks>
    public static Control CreateExpandHost(Control inner)
    {
        ArgumentNullException.ThrowIfNull(inner);

        var host = new Border
        {
            Child = inner,
            // 与内部控件初始可见性保持一致（如“女装”折叠栏默认隐藏）
            IsVisible = inner.IsVisible
        };

        if (IsEnabled)
        {
            var state = new ExpandHostState(host, inner);
            if (!HostStates.TryGetValue(inner, out _))
            {
                HostStates.Add(inner, state);
            }
            state.Attach();
        }

        return host;
    }

    /// <summary>被包裹控件 → 展开宿主状态（供 <see cref="RefreshExpandHost"/> 在内容变化时重算高度）。</summary>
    private static readonly ConditionalWeakTable<Control, ExpandHostState> HostStates = new();

    /// <summary>
    /// 通知展开宿主“内部内容尺寸可能已变化”（如折叠分组内的动态表格重建后）：
    /// 宿主高度是锁定值，不会自动跟随内容增长，不刷新会裁剪新内容。
    /// 未接入宿主（动画等级“禁用”）时为空操作。
    /// </summary>
    public static void RefreshExpandHost(Control inner)
    {
        if (HostStates.TryGetValue(inner, out var state))
        {
            state.RecomputeTarget();
        }
    }

    /// <summary>单个“平滑展开宿主”的状态与逻辑（宿主 Border 通过事件订阅持有本对象，随宿主一同存活）。</summary>
    private sealed class ExpandHostState
    {
        private readonly Border _host;
        private readonly Control _inner;
        private readonly PropertyInfo? _isExpandedProperty;
        private readonly PropertyInfo? _itemCountProperty;

        private bool _animationEnabled = true;
        private bool _initialized;

        // 目标展开状态：稳定态下与内部控件的 IsExpanded 一致；收起过渡期间内部会被引脚为展开
        private bool _desiredExpanded;
        // 收起过渡进行中（内部被引脚为展开，此时不能按内部实际尺寸测量折叠高度）
        private bool _collapsePending;
        // 屏蔽“宿主主动改写 IsExpanded”引发的回调，避免与用户点击混淆
        private bool _suppressInnerChange;

        // 折叠态自然高度（仅标题行）：收起时直接复用，避免收起瞬间测量到展开高度
        private double _collapsedHeight = double.NaN;
        private double _lastMeasuredWidth = double.NaN;

        private Expander? _innerExpander;
        // 内部内容站点：FA 主题为 Border#ExpanderContent，Avalonia 原生主题为 ContentPresenter#PART_ExpandSite
        private Control? _innerContent;
        private TranslateTransform? _contentTranslate;
        // 是否已挂上“内容为空时取消展开”的 Expanding 钩子（只挂一次）
        private bool _emptyExpandHooked;
        // 是否已执行“禁用内置动画”反射（重入 PrepareInnerExpander 时不再重复调用，避免对同一 Expander 重复 dispose）
        private bool _innerAnimationsDisabled;
        // PrepareInnerExpander 重试次数：嵌套模板（CompatSettingsExpander → host → SettingsExpander → 内部 Expander）
        // 在 host.Loaded 时内层 Expander 对象已生成、但其自身模板部件（ExpanderContent）可能尚未 build
        private int _prepareRetries;
        private const int MaxPrepareRetries = 5;
        private DispatcherTimer? _collapseCommitTimer;
        private DoubleTransition? _sizeTransition;
        // 展开后的自然高度（标题 + 内容）：内容滑动以此为基准
        private double _fullHeight = double.NaN;
        // 高度过渡的目标值：Transitions 生效时 Height 取值即“当前动画值”，
        // 不能拿它判断过渡是否结束 / 是否已到目标，必须自己记住目标值
        private double _heightTarget = double.NaN;
        // 内容滑动：手动插值时钟（与工具集其它动画一致，起点取当前位移，支持点击打断后续播）
        private DispatcherTimer? _contentTimer;
        private long _contentStartTicks;
        private double _contentDurationMs = 1;
        private double _contentFromY;
        private double _contentToY;
        private IEasing _contentEasing = ExpandSizeEasing;
        // 内容块是否已按“折叠态滑出”定位过：首次展开前需先归位，否则起点=终点、没有滑动动画
        private bool _contentSlideInitialized;

        public ExpandHostState(Border host, Control inner)
        {
            _host = host;
            _inner = inner;
            _isExpandedProperty = inner.GetType().GetProperty("IsExpanded");
            _itemCountProperty = inner.GetType().GetProperty("ItemCount");
        }

        public void Attach()
        {
            _desiredExpanded = ReadInnerExpanded();
            _host.ClipToBounds = true;
            _inner.PropertyChanged += OnInnerPropertyChanged;
            _host.PropertyChanged += OnHostPropertyChanged;
            _host.Loaded += OnLoaded;
            _host.Unloaded += OnUnloaded;
        }

        /// <summary>反射读取内部控件的 <c>IsExpanded</c> 值（FA2/FA3 均为公开属性）。</summary>
        private bool ReadInnerExpanded()
            => _isExpandedProperty?.GetValue(_inner) is true;

        /// <summary>被包裹控件是否没有子内容（ItemCount 为 0）：此时不允许展开。</summary>
        private bool IsContentEmpty()
            => _itemCountProperty?.GetValue(_inner) is int itemCount && itemCount <= 0;

        private void OnLoaded(object? sender, RoutedEventArgs e)
        {
            EnsureInitialized();

            // 内容为空（无子条目）时不做高度动画，但仍要接管内部动画：
            // PrepareInnerExpander 会挂 Expanding 取消钩子，让空折叠栏根本无法展开（含 FA 那条 1px 横线）。
            PrepareInnerExpander();
            RetryPrepareInnerExpanderIfNeeded();

            // 初始即展开的折叠栏（axaml IsExpanded="True"）从没进入过折叠态，
            // 等布局完成后补测一次折叠高度，避免首次收起时 host 不收缩、留下整块空白。
            Dispatcher.UIThread.Post(EnsureCollapsedHeightKnown, DispatcherPriority.Loaded);
        }

        private void OnUnloaded(object? sender, RoutedEventArgs e)
        {
            _collapseCommitTimer?.Stop();
            StopContentAnimation();
        }

        /// <summary>
        /// host.Loaded 时若内层 Expander 或其内容站点尚未生成，在后续 Background 帧重试（有上限）。
        /// 典型场景：CompatSettingsExpander 的模板链（包装宿主 → FA SettingsExpander → 内部 Expander）
        /// 逐层 build，Loaded 冒泡到宿主时内部 Expander 对象已存在、但其自身模板部件还没生成。
        /// </summary>
        private void RetryPrepareInnerExpanderIfNeeded()
        {
            if (_prepareRetries >= MaxPrepareRetries)
            {
                return;
            }

            if (_innerExpander != null && _contentTranslate != null)
            {
                return;
            }

            _prepareRetries++;
            Dispatcher.UIThread.Post(() =>
            {
                PrepareInnerExpander();
                RetryPrepareInnerExpanderIfNeeded();
            }, DispatcherPriority.Background);
        }

        /// <summary>
        /// 首次初始化：把容器高度锁定为当前自然高度（此时尚未装配过渡，因此不会播放动画），
        /// 之后展开 / 折叠才有可插值的起点。
        /// </summary>
        private void EnsureInitialized()
        {
            if (_initialized || !_animationEnabled)
            {
                return;
            }

            // 无子条目时 FA 会在展开时把 IsExpanded 强制夹回 false，与宿主的引脚逻辑冲突，直接退化
            if (_itemCountProperty?.GetValue(_inner) is int itemCount && itemCount <= 0)
            {
                _animationEnabled = false;
                return;
            }

            _initialized = true;

            var natural = MeasureInnerHeight();
            _host.Height = natural;
            _heightTarget = natural;
            if (!_desiredExpanded && !double.IsNaN(natural))
            {
                _collapsedHeight = natural;
            }

            _sizeTransition = new DoubleTransition
            {
                Property = Layoutable.HeightProperty,
                Duration = ExpandSizeDuration,
                Easing = ExpandSizeEasing
            };
            _host.Transitions = new Transitions { _sizeTransition };

            _collapseCommitTimer = new DispatcherTimer { Interval = CollapseSizeDuration };
            _collapseCommitTimer.Tick += OnCollapseCommit;
        }

        /// <summary>
        /// 接管被包裹控件内部的展开 / 折叠：禁用其自带动画（改由本宿主统一驱动），
        /// 并取出内容块（<c>Border#ExpanderContent</c>）接管其滑动 ——
        /// 展开时内容随容器高度一起向下滑入（底部贴着容器底部）；
        /// 收起时先播放内容上滑动画、容器高度暂不变，动画结束后再收起容器让下方组件上抬（与 WinUI 一致）。
        /// 找不到内部结构（如某些版本命名不同）时静默跳过，仅退化为“不额外接管”。
        /// </summary>
        private void PrepareInnerExpander()
        {
            // 被包裹控件本身可能就是 Expander（页面 axaml 直接声明的 Avalonia 内置 Expander），
            // GetVisualDescendants 不含自身，需先自检
            _innerExpander ??= _inner as Expander
                ?? _inner.GetVisualDescendants().OfType<Expander>().FirstOrDefault();
            if (_innerExpander == null)
            {
                return;
            }

            // 以下反射禁用只执行一次：本方法在“内层 Expander 模板部件延迟生成”时会被重试调用，
            // 重复 SetExpanderAnimationType(null) 等于对同一 Expander 重复 Dispose，FA2 上可能出错。
            if (!_innerAnimationsDisabled)
            {
                // 去掉 Expander 自带的展开 / 折叠内容交叉淡化
                try
                {
                    _innerExpander.GetType().GetProperty("ContentTransition")?.SetValue(_innerExpander, null);
                }
                catch (Exception ex)
                {
                    Log($"禁用内容过渡失败：{ex.Message}");
                }

                // 去掉 FluentAvalonia 自带的 WinUI 风格滑动动画（交由本宿主的裁剪动画统一接管）。
                // 注意 FA2 / FA3 的承载类型不同，必须分别尝试：
                //   · FA3：FluentAvalonia.UI.Controls.FAExpanderExt
                //   · FA2：FluentAvalonia.UI.Controls.ExpanderExt（ExpanderAnimationType = "FluentV2" 时启用，
                //          直接动画同一个 ExpanderContent 的 TranslateTransform；只关 FA3 会导致 net8 上
                //          两套动画抢同一属性 —— 折叠栏抽动 / 错位 / 卡住）
                foreach (var typeName in new[]
                         {
                             "FluentAvalonia.UI.Controls.FAExpanderExt, FluentAvalonia",
                             "FluentAvalonia.UI.Controls.ExpanderExt, FluentAvalonia"
                         })
                {
                    try
                    {
                        var extType = Type.GetType(typeName);
                        extType?.GetMethod("SetExpanderAnimationType", BindingFlags.Public | BindingFlags.Static)
                            ?.Invoke(null, new object?[] { _innerExpander, null });
                    }
                    catch (Exception ex)
                    {
                        Log($"禁用 FA 展开动画失败（{typeName}）：{ex.Message}");
                    }
                }

                _innerAnimationsDisabled = true;
            }

            // 内容为空时根本不允许进入展开态：在 IsExpanded 变化“之前”取消 Expanding。
            // 不能事后把 IsExpanded 同步夹回 false —— 那会让内部 Expander 的 :expanded
            // 伪类卡住（FA3 源码注释也提到必须 Post 才能正确清除），残留内容区顶部 1px 分隔线。
            if (!_emptyExpandHooked)
            {
                _innerExpander.Expanding += OnInnerExpanderExpanding;
                _emptyExpandHooked = true;
            }

            // 内容块随高度过渡一起平移：
            // FA 主题为 Border#ExpanderContent；Avalonia 原生 Expander 主题为 ContentPresenter#PART_ExpandSite
            if (_contentTranslate == null)
            {
                _innerContent = _innerExpander.GetVisualDescendants().OfType<Control>()
                    .FirstOrDefault(c => c.Name is "ExpanderContent" or "PART_ExpandSite");
                if (_innerContent != null)
                {
                    _contentTranslate = new TranslateTransform();
                    _innerContent.RenderTransform = _contentTranslate;

                    // 内容站点可能在 Loaded 之后的重试帧才找到（嵌套模板延迟 build）：
                    // OnLoaded 那次折叠高度补测会因 _innerContent==null 跳过，这里立即补测一次。
                    EnsureCollapsedHeightKnown();
                }
            }

            // FA2 的 ExpanderExt 原本通过 :noAnimation 伪类控制 ExpanderContent 的可见性；
            // 关掉它后伪类不再切换，折叠态内容站点会回到“默认可见”（内部测量仍含内容，
            // 折叠后标题下方留出空白）。改由宿主按 IsExpanded 显式接管其可见性：
            // 展开 / 收起过程中保持可见（由 host 高度裁剪），真实折叠后置 false。
            if (_innerContent != null)
            {
                _innerContent.IsVisible = ReadInnerExpanded();

                // FA2 由 ExpanderExt 负责给内容裁剪容器（ExpanderContentClip）设 ClipToBounds，
                // 关掉它后这份裁剪缺失，收起时内容上滑会盖到标题上。这里补上，让内容严格裁在内容区内。
                var clip = _innerExpander.GetVisualDescendants().OfType<Border>()
                    .FirstOrDefault(border => border.Name == "ExpanderContentClip");
                if (clip != null)
                {
                    clip.ClipToBounds = true;
                }
                else
                {
                    _innerContent.ClipToBounds = true;
                }
            }

            Log("已接管内部折叠栏动画");
        }

        /// <summary>
        /// 内部 Expander 即将展开（coerce 阶段，即便新旧值相同也会触发）：
        /// 1) 内容为空（无子条目）时直接取消，IsExpanded 不会变为 true —— 标题不翻转、无 1px 分隔线；
        /// 2) 收起过渡途中内部被引脚为展开，用户再点“展开”不会产生 IsExpanded 变化事件，
        ///    借这里把目标状态补翻转为展开。
        /// </summary>
        private void OnInnerExpanderExpanding(object? sender, CancelRoutedEventArgs e)
        {
            if (IsContentEmpty())
            {
                e.Cancel = true;
                e.Handled = true;
                return;
            }

            if (!_suppressInnerChange && !_desiredExpanded)
            {
                _desiredExpanded = true;
                Log("Expanding 补翻转：目标=展开");
                Dispatcher.UIThread.Post(ApplyDesiredState, DispatcherPriority.Loaded);
            }
        }

        private void OnInnerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            // 折叠栏整体被外部隐藏（如“女装”入口）时，宿主跟随
            if (e.Property.Name == "IsVisible")
            {
                _host.IsVisible = _inner.IsVisible;
                if (_inner.IsVisible && _animationEnabled)
                {
                    Dispatcher.UIThread.Post(RecomputeTarget, DispatcherPriority.Loaded);
                }
                return;
            }

            // 内容为空时不允许展开。正常情况下 Expanding 钩子已在属性变化前取消，根本走不到这里；
            // 这里仅作为钩子未生效时的兜底（如内部模板结构不同）：仿 FA3 的做法 Post 到 Send
            // 优先级再夹回 false —— 不能同步夹回，否则内部 Expander 的 :expanded 伪类会卡住，
            // 残留内容区顶部约 1px 的分隔线。
            if (e.Property.Name == "IsExpanded" && !_suppressInnerChange && IsContentEmpty())
            {
                if (ReadInnerExpanded())
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (IsContentEmpty() && ReadInnerExpanded())
                        {
                            PinInnerExpanded(false);
                        }
                    }, DispatcherPriority.Send);
                }
                return;
            }

            if (!_animationEnabled || e.Property.Name != "IsExpanded" || _suppressInnerChange)
            {
                return;
            }

            // 以内部 IsExpanded 的实际值同步目标状态：
            // · 稳定态点击（false↔true 真正变化）：内部值即用户目标；
            // · 收起途中点“展开”（内部一直被引脚为 true）：值未变、没有事件，由 Expanding 钩子处理；
            //   若钩子已先行翻转，这里读到的值与目标一致，直接跳过，避免重复翻转。
            var nowExpanded = ReadInnerExpanded();
            if (nowExpanded == _desiredExpanded)
            {
                return;
            }

            _desiredExpanded = nowExpanded;
            Log($"IsExpanded 变化：{e.OldValue} -> {e.NewValue}，目标={_desiredExpanded}，当前 Height={_host.Height}");
            Dispatcher.UIThread.Post(ApplyDesiredState, DispatcherPriority.Loaded);
        }

        private void OnHostPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (!_animationEnabled || e.Property != Visual.BoundsProperty)
            {
                return;
            }

            var width = _host.Bounds.Width;
            if (double.IsNaN(_lastMeasuredWidth) || Math.Abs(width - _lastMeasuredWidth) > 0.5)
            {
                _lastMeasuredWidth = width;
                Dispatcher.UIThread.Post(RecomputeTarget, DispatcherPriority.Loaded);

                // 宽度首次有效时也补测折叠高度（首帧 Loaded 时可能还没布局出宽度）
                if (_desiredExpanded)
                {
                    Dispatcher.UIThread.Post(EnsureCollapsedHeightKnown, DispatcherPriority.Loaded);
                }
            }
        }

        /// <summary>按当前目标状态推进展开 / 折叠（容器高度用过渡，内容滑动用本工具集的手动插值时钟）。</summary>
        private void ApplyDesiredState()
        {
            if (!_animationEnabled)
            {
                return;
            }
            if (!_initialized)
            {
                EnsureInitialized();
                if (!_initialized)
                {
                    return;
                }
            }

            if (_desiredExpanded)
            {
                _collapsePending = false;
                _collapseCommitTimer?.Stop();
                PinInnerExpanded(true);

                // 展开过程中内容必须挂载可见（由 host 高度逐段露出）
                if (_innerContent != null)
                {
                    _innerContent.IsVisible = true;
                }

                ApplySizeTransition(true);
                SetHeight(ResolveTargetHeight());

                // 首次展开：内容块还没处于折叠态位移（此时位移为 0），先归位到起点，
                // 否则滑动动画起点与终点相同会被跳过 —— 首帧看不到内容贴合底部滑入。
                if (!_contentSlideInitialized && _contentTranslate != null)
                {
                    _contentTranslate.Y = -GetContentSlideDistance();
                    _contentSlideInitialized = true;
                }

                // 内容随容器一起向下滑入（与容器底部同步）
                StartContentAnimation(0, ExpandSizeDuration, ExpandSizeEasing);
            }
            else
            {
                _collapsePending = true;
                // 保持内容挂载：把内部重新引脚为展开，收起过程中内容仍可被滑动 / 裁剪
                PinInnerExpanded(true);
                if (_innerContent != null)
                {
                    _innerContent.IsVisible = true;
                }

                // 与展开互为镜像：内容上滑与容器高度收起同时进行，下方组件同步上抬
                ApplySizeTransition(false);
                SetHeight(ResolveTargetHeight());

                StartContentAnimation(-GetContentSlideDistance(), CollapseSizeDuration, CollapseSizeEasing);
                _contentSlideInitialized = true;

                // 过渡结束后再真正折叠内部控件
                _collapseCommitTimer?.Stop();
                _collapseCommitTimer?.Start();
            }
        }

        /// <summary>维持当前状态的高度（尺寸 / 可见性变化时调用，不改变展开状态）。</summary>
        internal void RecomputeTarget()
        {
            if (!_animationEnabled)
            {
                return;
            }
            if (!_initialized)
            {
                EnsureInitialized();
                if (!_initialized)
                {
                    return;
                }
            }

            ApplySizeTransition(_desiredExpanded);
            SetHeight(ResolveTargetHeight());
        }

        /// <summary>
        /// 确保已缓存“折叠态自然高度”。折叠高度只有在内容真正折叠时测得准；初始即展开
        /// （axaml <c>IsExpanded="True"</c>）的折叠栏从未进入过折叠态，首次收起时内部被引脚展开，
        /// 直接测量会得到展开全高 —— host 高度目标不变、收起后留下整块空白。
        /// 这里临时隐藏内容站点做一次离屏测量并立即恢复（同一同步流程内，无布局 / 渲染帧，
        /// 用户看不到任何变化），把标题行高度缓存下来。
        /// </summary>
        private void EnsureCollapsedHeightKnown()
        {
            if (!_animationEnabled || !double.IsNaN(_collapsedHeight) || _innerContent == null)
            {
                return;
            }

            var previousVisible = _innerContent.IsVisible;
            try
            {
                _innerContent.IsVisible = false;
                var measured = MeasureInnerHeight();
                if (!double.IsNaN(measured) && measured > 0)
                {
                    _collapsedHeight = measured;
                }
            }
            finally
            {
                _innerContent.IsVisible = previousVisible;
            }
        }

        /// <summary>按展开状态计算目标高度；静止折叠态测量可靠，可顺带刷新折叠高度缓存。</summary>
        private double ResolveTargetHeight()
        {
            if (_desiredExpanded)
            {
                var expanded = MeasureInnerHeight();
                if (!double.IsNaN(expanded))
                {
                    _fullHeight = expanded;
                }
                return expanded;
            }

            if (_collapsePending && !double.IsNaN(_collapsedHeight))
            {
                return _collapsedHeight;
            }

            var measured = MeasureInnerHeight();
            if (!double.IsNaN(measured))
            {
                _collapsedHeight = measured;
            }
            return measured;
        }

        private void SetHeight(double target)
        {
            if (double.IsNaN(target))
            {
                return;
            }

            // 与“目标值”比较（而非 Height —— 过渡期间 Height 是当前动画值，会误判为已到目标）
            if (!double.IsNaN(_heightTarget) && Math.Abs(target - _heightTarget) < 0.5)
            {
                return;
            }

            _heightTarget = target;
            _host.Height = target;
        }

        /// <summary>
        /// 按展开方向切换高度过渡的时长与缓动：展开 333ms / KeySpline(0,0,0,1)，
        /// 收起 167ms / KeySpline(1,1,0,1)，与 ClassIsland 的 Expander 一致。
        /// </summary>
        private void ApplySizeTransition(bool expanded)
        {
            if (_sizeTransition == null)
            {
                return;
            }

            _sizeTransition.Duration = expanded ? ExpandSizeDuration : CollapseSizeDuration;
            _sizeTransition.Easing = expanded ? ExpandSizeEasing : CollapseSizeEasing;
        }

        /// <summary>内容块需要滑动的距离（= 内容高度 = 展开高度 − 折叠高度）。</summary>
        private double GetContentSlideDistance()
        {
            if (double.IsNaN(_fullHeight))
            {
                return 0;
            }

            var collapsed = double.IsNaN(_collapsedHeight) ? 0 : _collapsedHeight;
            var distance = _fullHeight - collapsed;
            return distance > 0 ? distance : _fullHeight;
        }

        /// <summary>
        /// 启动内容块的滑动动画（手动插值时钟；起点取当前实际位移，点击打断后可从当前位置续播）：
        /// 展开滑到 0、收起滑到 −内容高度，均与容器高度过渡同时进行，使内容底部贴着容器底部一起移动。
        /// </summary>
        private void StartContentAnimation(double toY, TimeSpan duration, IEasing easing)
        {
            if (_contentTranslate == null)
            {
                return;
            }

            var fromY = double.IsNaN(_contentTranslate.Y) ? 0 : _contentTranslate.Y;
            if (Math.Abs(toY - fromY) < 0.5)
            {
                _contentTranslate.Y = toY;
                StopContentAnimation();
                return;
            }

            _contentFromY = fromY;
            _contentToY = toY;
            _contentDurationMs = Math.Max(1, duration.TotalMilliseconds);
            _contentEasing = easing;
            _contentStartTicks = Stopwatch.GetTimestamp();

            if (_contentTimer != null)
            {
                return;
            }

            _contentTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = ScrollFrameInterval };
            _contentTimer.Tick += OnContentTick;
            _contentTimer.Start();
        }

        private void OnContentTick(object? sender, EventArgs e)
        {
            if (_contentTranslate == null)
            {
                StopContentAnimation();
                return;
            }

            var elapsedMs = (Stopwatch.GetTimestamp() - _contentStartTicks) * 1000.0 / Stopwatch.Frequency;
            var progress = Math.Clamp(elapsedMs / _contentDurationMs, 0.0, 1.0);
            var eased = _contentEasing.Ease(progress);
            _contentTranslate.Y = _contentFromY + (_contentToY - _contentFromY) * eased;

            if (progress >= 1.0)
            {
                _contentTranslate.Y = _contentToY;
                StopContentAnimation();
            }
        }

        private void StopContentAnimation()
        {
            if (_contentTimer == null)
            {
                return;
            }

            _contentTimer.Stop();
            _contentTimer.Tick -= OnContentTick;
            _contentTimer = null;
        }

        /// <summary>收起过渡结束时真正折叠内部控件（内容此时已随容器一起滑出 / 裁掉，视觉上无跳变）。</summary>
        private void OnCollapseCommit(object? sender, EventArgs e)
        {
            _collapseCommitTimer?.Stop();
            _collapsePending = false;

            if (_desiredExpanded)
            {
                return;
            }

            PinInnerExpanded(false);

            // 不能在这里立即测量并“带动画”校正：
            // 置 IsExpanded=false 后，内部 Expander / 模板绑定与布局要到之后的布局周期才生效，
            // 立即 Measure 会拿到“半折叠”的中间高度（接近展开全高），再经高度过渡反向播放，
            // 表现为“收起后又打开一点”、标题下方留下一块空白。
            // 改为等本轮布局完成后，隐藏内容站点、用真实折叠高度“无过渡吸附”落位。
            Dispatcher.UIThread.Post(() =>
            {
                // 期间用户又展开了：放弃校正
                if (_desiredExpanded || ReadInnerExpanded())
                {
                    return;
                }

                // 真实折叠：内容站点隐藏（展开时再置回 true），此后内部测量才是纯标题高度
                if (_innerContent != null)
                {
                    _innerContent.IsVisible = false;
                }

                var measured = MeasureInnerHeight();
                if (!double.IsNaN(measured) && measured > 0)
                {
                    _collapsedHeight = measured;
                    SnapHeight(measured);
                }
            }, DispatcherPriority.Background);
        }

        /// <summary>
        /// 直接把宿主高度吸附到指定值（不带过渡），用于收起提交后的精确落位；
        /// 校正差通常只有几像素，瞬时吸附不突兀；若带动画反而会“又展开一点”露出空白。
        /// </summary>
        private void SnapHeight(double target)
        {
            if (double.IsNaN(target))
            {
                return;
            }

            var transitions = _host.Transitions;
            _host.Transitions = null;
            _heightTarget = target;
            _host.Height = target;
            _host.Transitions = transitions;
        }

        /// <summary>把内部控件的 IsExpanded 引脚到指定值（不视为用户点击）。</summary>
        private void PinInnerExpanded(bool expanded)
        {
            if (_isExpandedProperty == null || ReadInnerExpanded() == expanded)
            {
                return;
            }

            _suppressInnerChange = true;
            try
            {
                _isExpandedProperty.SetValue(_inner, expanded);
            }
            finally
            {
                _suppressInnerChange = false;
            }
        }

        /// <summary>在“按可用宽度换行”的前提下测量被包裹控件的自然高度。</summary>
        private double MeasureInnerHeight()
        {
            var width = _host.Bounds.Width;
            if (width <= 0 && _host.Parent is Layoutable parent && parent.Bounds.Width > 0)
            {
                width = parent.Bounds.Width;
            }
            if (width <= 0)
            {
                width = _lastMeasuredWidth;
            }
            if (width <= 0 || double.IsNaN(width))
            {
                width = double.PositiveInfinity;
            }

            // Avalonia 会缓存相同 availableSize 的测量结果：若直接重复测量，收起时会命中展开时的缓存。
            // 因此先用一个略有差异的宽度测一次使缓存失效，再用真实宽度取得最新高度。
            if (width > 0 && !double.IsInfinity(width))
            {
                _inner.Measure(new Size(width + 0.5, double.PositiveInfinity));
            }
            _inner.Measure(new Size(width, double.PositiveInfinity));

            var height = _inner.DesiredSize.Height;
            return double.IsNaN(height) || height < 0 ? double.NaN : height;
        }
    }

    private static void Log(string message)
        => Logger?.LogInformation("[IntroAnimation] {Message}", message);
}
