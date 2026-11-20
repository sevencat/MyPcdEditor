using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Rendering;
using Avalonia.Threading;
using MyPcdEditor.Rendering.Gles3;
using Silk.NET.Core.Contexts;
using Silk.NET.OpenGLES;
using AvaloniaGlInterface = Avalonia.OpenGL.GlInterface;

namespace MyPcdEditor.App.Rendering;

/// <summary>
/// 所有 GL 控件的基类：把 Avalonia 的 ANGLE 上下文接到 Silk.NET 上，并提供帧泵、尺寸/DPI 跟踪、
/// 指针/滚轮/键盘桥接。
/// </summary>
/// <remarks>
/// <para>约束见 docs/architecture.md §3，逐条对应：</para>
/// <list type="bullet">
/// <item>GL 资源只在渲染回调里创建/使用/释放。</item>
/// <item>每帧自己绑 FBO、设 viewport，末尾恢复 Avalonia 期望的 GL 状态。</item>
/// <item>必须实现 <see cref="ICustomHitTest"/>，否则整块区域收不到指针事件。</item>
/// <item>静态 GL 入口的清空要带 generation 守卫。</item>
/// </list>
/// </remarks>
public abstract class GlControlBase : OpenGlControlBase, ICustomHitTest
{
    /// <summary>单帧 delta 上限：断点/最小化回来时不要给相机一个巨大的时间跳变。</summary>
    private const double MaxDeltaSeconds = 0.25;

    private static int _liveCount;
    private static int _currentGeneration;
    private static int _generationSeed;

    private readonly int _generation = Interlocked.Increment(ref _generationSeed);
    private readonly Stopwatch _clock = new();

    private GL? _gl;
    private long _lastTicks;
    private double _fpsTimer;
    private int _fpsFrames;
    private double _elapsedSeconds;

    private MouseButton? _dragButton;
    private double _lastX;
    private double _lastY;
    private bool _renderFaultReported;

    protected GlControlBase()
    {
        Focusable = true;
    }

    /// <summary>当前活着的 GL 控件数（诊断用）。</summary>
    public static int LiveCount => Volatile.Read(ref _liveCount);

    /// <summary>命中测试：整块控件区域都算命中。</summary>
    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    /// <summary>当前 GL 入口（仅在渲染回调内有意义）。</summary>
    protected GL Gl => _gl ?? throw new InvalidOperationException("GL 入口尚未初始化。");

    /// <summary>渲染目标像素宽（含 DPI 缩放）。</summary>
    public int PixelWidth { get; private set; }

    /// <summary>渲染目标像素高（含 DPI 缩放）。</summary>
    public int PixelHeight { get; private set; }

    public double Fps { get; private set; }

    public long FrameIndex { get; private set; }

    /// <summary>上下文能力快照（<see cref="OnGlReady"/> 之后可用）。</summary>
    public GlInfo? Info { get; private set; }

    /// <summary>指针归一化坐标（0..1，左上原点）。</summary>
    public (double X, double Y)? Pointer { get; private set; }

    /// <summary>每秒最多触发一次，用于刷状态栏。</summary>
    public event Action<GlControlBase>? FrameStatsUpdated;

    /// <summary>上下文与 GL 入口就绪（在此建资源）。</summary>
    protected abstract void OnGlReady();

    /// <summary>画一帧。</summary>
    protected abstract void OnFrame(double deltaSeconds, double elapsedSeconds);

    /// <summary>释放 GL 资源（上下文仍然 current）。</summary>
    protected abstract void OnGlReleased();

    protected virtual void OnSizeChanged(int pixelWidth, int pixelHeight)
    {
    }

    protected virtual void OnPointerMoved(double normalizedX, double normalizedY)
    {
    }

    protected virtual void OnPointerPressed(double normalizedX, double normalizedY, MouseButton button)
    {
    }

    protected virtual void OnPointerReleased(double normalizedX, double normalizedY, MouseButton button)
    {
    }

    /// <summary>拖动：<paramref name="deltaX"/>/<paramref name="deltaY"/> 是像素位移。</summary>
    protected virtual void OnPointerDragged(double normalizedX, double normalizedY, double deltaX, double deltaY, MouseButton button)
    {
    }

    /// <summary>滚轮（<paramref name="delta"/> &gt; 0 向前滚）。</summary>
    protected virtual void OnScroll(double normalizedX, double normalizedY, double delta)
    {
    }

    protected virtual void OnKeyChanged(Key key, bool down)
    {
    }

    // ---- 上下文生命周期 ----

    protected sealed override void OnOpenGlInit(AvaloniaGlInterface glInterface)
    {
        _gl = new GL(new LamdaNativeContext(glInterface.GetProcAddress));
        GlContext.Initialize(_gl);

        Interlocked.Exchange(ref _currentGeneration, _generation);
        Interlocked.Increment(ref _liveCount);

        Info = GlInfo.Query(_gl);
        Console.WriteLine("[GL] " + Info.Describe());

        _clock.Restart();
        _lastTicks = _clock.ElapsedTicks;

        UpdatePixelSize(force: true);
        OnGlReady();

        RequestNextFrameRendering();
    }

    protected sealed override void OnOpenGlRender(AvaloniaGlInterface glInterface, int fb)
    {
        GL gl = Gl;

        UpdatePixelSize(force: false);

        gl.BindFramebuffer(GLEnum.Framebuffer, (uint)fb);
        gl.Viewport(0, 0, (uint)PixelWidth, (uint)PixelHeight);
        gl.Disable(EnableCap.ScissorTest);
        gl.ColorMask(true, true, true, true);

        long now = _clock.ElapsedTicks;
        double delta = Math.Clamp((now - _lastTicks) / (double)Stopwatch.Frequency, 0, MaxDeltaSeconds);
        _lastTicks = now;
        _elapsedSeconds += delta;

        FrameIndex++;
        try
        {
            OnFrame(delta, _elapsedSeconds);
        }
        catch (Exception ex) when (!_renderFaultReported)
        {
            // 渲染回调里抛异常会被合成器吞掉，不主动打日志就只能看到黑屏
            _renderFaultReported = true;
            Console.WriteLine($"[GL] 渲染回调异常：{ex}");
        }

        // 恢复 Avalonia/Skia 期望的状态，否则后续合成会花屏
        gl.BindFramebuffer(GLEnum.Framebuffer, (uint)fb);
        gl.UseProgram(0);
        gl.BindVertexArray(0);
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.Disable(EnableCap.DepthTest);

        UpdateFps(delta);

        RequestNextFrameRendering();
    }

    protected sealed override void OnOpenGlDeinit(AvaloniaGlInterface glInterface)
    {
        try
        {
            OnGlReleased();
        }
        finally
        {
            Interlocked.Decrement(ref _liveCount);

            // 旧控件的 Deinit 可能晚于新控件的 Init：只有"最新的那个"能清全局入口
            if (Interlocked.CompareExchange(ref _currentGeneration, 0, _generation) == _generation)
            {
                GlContext.Reset();
            }

            _gl?.Dispose();
            _gl = null;
            _clock.Stop();

            Console.WriteLine($"[GL] 控件释放，存活 {LiveCount} 个");
        }
    }

    // ---- 输入 ----

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (!TryNormalize(e.GetPosition(this), out double x, out double y))
        {
            return;
        }

        Pointer = (x, y);
        OnPointerMoved(x, y);

        if (_dragButton is { } button)
        {
            double deltaX = e.GetPosition(this).X - _lastX;
            double deltaY = e.GetPosition(this).Y - _lastY;
            _lastX = e.GetPosition(this).X;
            _lastY = e.GetPosition(this).Y;

            if (deltaX != 0 || deltaY != 0)
            {
                OnPointerDragged(x, y, deltaX, deltaY, button);
            }
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();

        PointerPoint point = e.GetCurrentPoint(this);
        if (!TryNormalize(point.Position, out double x, out double y))
        {
            return;
        }

        _dragButton = point.Properties.PointerUpdateKind switch
        {
            PointerUpdateKind.RightButtonPressed => MouseButton.Right,
            PointerUpdateKind.MiddleButtonPressed => MouseButton.Middle,
            _ => MouseButton.Left,
        };

        _lastX = point.Position.X;
        _lastY = point.Position.Y;

        // 抓指针：拖到控件外松手也要收到 release，否则"按住"状态会一直留着
        e.Pointer.Capture(this);

        Pointer = (x, y);
        OnPointerPressed(x, y, _dragButton.Value);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (!TryNormalize(e.GetPosition(this), out double x, out double y))
        {
            return;
        }

        MouseButton button = _dragButton ?? MouseButton.Left;
        _dragButton = null;

        Pointer = (x, y);
        OnPointerReleased(x, y, button);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragButton = null;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        if (TryNormalize(e.GetPosition(this), out double x, out double y))
        {
            OnScroll(x, y, e.Delta.Y);
            e.Handled = true;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        OnKeyChanged(e.Key, down: true);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        OnKeyChanged(e.Key, down: false);
    }

    private bool TryNormalize(Point position, out double x, out double y)
    {
        double width = Bounds.Width;
        double height = Bounds.Height;

        if (width <= 0 || height <= 0)
        {
            x = 0;
            y = 0;
            return false;
        }

        x = Math.Clamp(position.X / width, 0, 1);
        y = Math.Clamp(position.Y / height, 0, 1);
        return true;
    }

    // ---- 尺寸 / 统计 ----

    private void UpdatePixelSize(bool force)
    {
        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        int width = Math.Max(1, (int)Math.Round(Bounds.Width * scaling));
        int height = Math.Max(1, (int)Math.Round(Bounds.Height * scaling));

        if (!force && width == PixelWidth && height == PixelHeight)
        {
            return;
        }

        PixelWidth = width;
        PixelHeight = height;
        OnSizeChanged(width, height);
    }

    private void UpdateFps(double delta)
    {
        _fpsFrames++;
        _fpsTimer += delta;

        if (_fpsTimer < 1.0)
        {
            return;
        }

        Fps = _fpsFrames / _fpsTimer;
        _fpsFrames = 0;
        _fpsTimer = 0;

        // 冒烟/基准用：PGV_TRACE_FPS=1 时把 FPS 打到控制台，便于无界面采集
        if (Environment.GetEnvironmentVariable("PGV_TRACE_FPS") == "1")
        {
            string extra = this is Controls.PointCloudView view
                ? $" 上传中={view.IsUploading} 进度={view.UploadProgress:P0} 帧={FrameIndex}" +
                  $" 块={view.VisibleChunkCount}/{view.TotalChunkCount} 绘制={view.DrawnPointCount}"
                : string.Empty;
            Console.WriteLine($"[FPS] {Fps:F1}  {PixelWidth}x{PixelHeight}{extra}");
        }

        Action<GlControlBase>? handler = FrameStatsUpdated;
        if (handler is not null)
        {
            // 事件来自渲染线程，切回 UI 线程再刷界面
            Dispatcher.UIThread.Post(() => handler(this), DispatcherPriority.Background);
        }
    }
}
