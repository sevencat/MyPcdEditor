using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using MyPcdEditor.App.Controls;
using MyPcdEditor.App.Views;
using MyPcdEditor.Core.PointCloud;
using MyPcdEditor.Rendering.Gles3;
using Matrix4x4 = System.Numerics.Matrix4x4;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace MyPcdEditor.App.SelfTest;

/// <summary>
/// <c>--selftest</c> 的执行体：加载地图包 → 等 GPU 上传完成 → 多画几帧 → 读回像素 → 判定并退出。
/// </summary>
/// <remarks>
/// 这是 headless 测不到的那条路径（GL 上下文、着色器、实际绘制、像素），
/// 用于在无人值守时发现"黑屏/色带失效/只画了背景"这类渲染回归。
/// </remarks>
internal sealed class SelfTestSession
{
    /// <summary>整体超时：371 MB 的点云在集显上上传 + 首帧也用不了这么久。</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(180);

    /// <summary>上传完成后再画几帧才读回，避开"刚上传完那一帧"的时序抖动。</summary>
    private const long SettleFrames = 3;

    /// <summary>视野判定抽样的点数（与相机拟合用的量级一致）。</summary>
    private const int TraceSampleCount = 8192;

    /// <summary>等后台分块包围盒的上限：32 M 点 Debug 下约 2 s，超时就不等了（不能卡死自检）。</summary>
    private static readonly TimeSpan ChunkWaitLimit = TimeSpan.FromSeconds(15);

    /// <summary>
    /// <c>PGV_SELFTEST_PARTIAL_VIEW=1</c>：允许点云超出视野。
    /// 配合 <c>PGV_CAM_ZOOM</c> 用来看"放大到局部"时剔除前后的画面是否一致。
    /// </summary>
    private static readonly bool AllowPartialView =
        Environment.GetEnvironmentVariable("PGV_SELFTEST_PARTIAL_VIEW") == "1";

    private readonly MainWindow _window;
    private readonly PointCloudView _view;
    private readonly MapView _map;
    private readonly string _directory;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private readonly DispatcherTimer _poll;

    private TimeSpan _uploadCompleteAt;
    private long _frameAtUploadComplete = -1;
    private bool _captureRequested;
    private bool _finished;
    private bool _boundsOutOfView;
    private bool _chunkWaitLogged;

    public SelfTestSession(MainWindow window, PointCloudView view, string directory)
    {
        _window = window;
        _view = view;
        _map = window.MapViewControl;
        _directory = directory;
        _poll = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, OnPoll);
    }

    public void Start()
    {
        Console.WriteLine($"[SELFTEST] 开始：{_directory}");

        _view.CloudReady += OnCloudReady;
        _view.PixelCaptured += OnPixelCaptured;
        _poll.Start();

        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        bool loaded = await _window.LoadPackageAsync(_directory);
        if (!loaded)
        {
            Finish(false, "加载失败（目录内没有可读的 .pcd，或读取时抛异常）");
            return;
        }

        Console.WriteLine($"[SELFTEST] 点云已提交上传，等待 GPU 上传完成…（累计 {_watch.ElapsedMilliseconds} ms）");
    }

    private void OnCloudReady(XyzCloud cloud)
    {
        _uploadCompleteAt = _watch.Elapsed;
        _frameAtUploadComplete = _view.FrameIndex;

        Console.WriteLine(
            $"[SELFTEST] 上传完成：{cloud.PointCount:N0} 点 / {cloud.ByteLength / 1024 / 1024} MB / " +
            $"{_uploadCompleteAt.TotalMilliseconds:F0} ms");
        Console.WriteLine(
            $"[SELFTEST] 包围盒：Min={cloud.Bounds.Min} Max={cloud.Bounds.Max} " +
            $"尺寸={cloud.Bounds.Size:F1}");
        Console.WriteLine(
            $"[SELFTEST] 相机：Target={_view.Camera.Target:F2} Distance={_view.Camera.Distance:F1} " +
            $"Yaw={_view.Camera.Yaw * 180f / MathF.PI:F1}° Pitch={_view.Camera.Pitch * 180f / MathF.PI:F1}°");
        Console.WriteLine(_map.HasMap
            ? $"[SELFTEST] 2D 地图：已加载 · {_map.PixelsPerMeter:F2} px/m · 叠加 {_map.OverlayCount:N0} 点"
            : "[SELFTEST] 2D 地图：未加载");

        (int groundWidth, int groundHeight) = _view.GroundTextureSize;
        Console.WriteLine(_view.GroundUploaded
            ? $"[SELFTEST] 地面纹理：已上传 {groundWidth}×{groundHeight}"
            : "[SELFTEST] 地面纹理：尚未上传（可能仍在等点云上传完成）");
        Console.WriteLine(_view.TotalChunkCount > 0
            ? $"[SELFTEST] 分块剔除：{_view.TotalChunkCount} 块 · 视锥剔除{(_view.CullingEnabled ? "开" : "关")}"
            : "[SELFTEST] 分块剔除：未分块（后台仍在算，读回前会等它）");
        TraceBoundsProjection(cloud);
    }

    /// <summary>
    /// 把抽样点投影到 NDC，确认相机真的框住了点云。
    /// </summary>
    /// <remarks>
    /// 判定用抽样点而不是包围盒角点：斜置 / 细长的点云，角点常常落在数据之外，
    /// 按角点判定会误报"超出视野"。角点范围只打印出来作参考。
    /// </remarks>
    private void TraceBoundsProjection(XyzCloud cloud)
    {
        float aspect = _view.PixelHeight > 0 ? _view.PixelWidth / (float)_view.PixelHeight : 1.6f;
        Matrix4x4 viewProjection = _view.Camera.GetViewProjection(aspect);

        Bounds3 bounds = cloud.Bounds;
        Span<Vector3> corners = stackalloc Vector3[8];
        for (int i = 0; i < 8; i++)
        {
            corners[i] = new Vector3(
                (i & 1) == 0 ? bounds.Min.X : bounds.Max.X,
                (i & 2) == 0 ? bounds.Min.Y : bounds.Max.Y,
                (i & 4) == 0 ? bounds.Min.Z : bounds.Max.Z);
        }

        (float MinX, float MaxX, float MinY, float MaxY, bool Any, bool Behind) cornerNdc =
            ProjectToNdc(corners, viewProjection);
        (float MinX, float MaxX, float MinY, float MaxY, bool Any, bool Behind) pointNdc =
            ProjectToNdc(cloud.SamplePoints(TraceSampleCount), viewProjection);

        // 允许 2% 的越界（浮点与拟合余量），再多就说明 Fit 没生效
        const float tolerance = 1.02f;
        _boundsOutOfView = !pointNdc.Any
                           || pointNdc.Behind
                           || pointNdc.MinX < -tolerance || pointNdc.MaxX > tolerance
                           || pointNdc.MinY < -tolerance || pointNdc.MaxY > tolerance;

        if (cornerNdc.Any)
        {
            Console.WriteLine(
                $"[SELFTEST] 包围盒角点 NDC 范围（参考）：x=[{cornerNdc.MinX:F2}, {cornerNdc.MaxX:F2}] " +
                $"y=[{cornerNdc.MinY:F2}, {cornerNdc.MaxY:F2}]");
        }

        if (!pointNdc.Any)
        {
            Console.WriteLine("[SELFTEST] 抽样点全部落在相机背后，视野判定失败");
            return;
        }

        Console.WriteLine(
            $"[SELFTEST] 抽样点 NDC 范围：x=[{pointNdc.MinX:F2}, {pointNdc.MaxX:F2}] " +
            $"y=[{pointNdc.MinY:F2}, {pointNdc.MaxY:F2}]" +
            $"（-1..1 为可见，aspect={aspect:F2}，{(_boundsOutOfView ? "已超出视野" : "完整在视野内")}）");
    }

    /// <summary>把一批世界坐标点投到 NDC，返回范围（含"是否有可见点 / 是否有在相机背后的点"）。</summary>
    private static (float MinX, float MaxX, float MinY, float MaxY, bool Any, bool Behind) ProjectToNdc(
        ReadOnlySpan<Vector3> points,
        Matrix4x4 viewProjection)
    {
        float minX = float.MaxValue, maxX = float.MinValue;
        float minY = float.MaxValue, maxY = float.MinValue;
        bool any = false;
        bool behind = false;

        foreach (Vector3 point in points)
        {
            Vector4 clip = Vector4.Transform(new Vector4(point, 1f), viewProjection);
            if (clip.W <= 1e-4f)
            {
                behind = true;
                continue;
            }

            any = true;
            float x = clip.X / clip.W;
            float y = clip.Y / clip.W;
            minX = MathF.Min(minX, x);
            maxX = MathF.Max(maxX, x);
            minY = MathF.Min(minY, y);
            maxY = MathF.Max(maxY, y);
        }

        return (minX, maxX, minY, maxY, any, behind);
    }

    private void OnPoll(object? sender, EventArgs e)
    {
        if (_finished)
        {
            return;
        }

        if (_watch.Elapsed > Timeout)
        {
            Finish(false, $"超时 {Timeout.TotalSeconds:F0} s（上传中={_view.IsUploading} 进度={_view.UploadProgress:P0}）");
            return;
        }

        if (_captureRequested || _frameAtUploadComplete < 0)
        {
            return;
        }

        if (_view.IsUploading || _view.FrameIndex - _frameAtUploadComplete < SettleFrames)
        {
            return;
        }

        // 分块包围盒在后台算：等它接管再读回，否则测到的还是"不分块、整块画"那条路径
        if (_view.PointCount > 0 && _view.TotalChunkCount == 0 && _watch.Elapsed < ChunkWaitLimit)
        {
            if (!_chunkWaitLogged)
            {
                _chunkWaitLogged = true;
                Console.WriteLine("[SELFTEST] 等待后台分块包围盒…");
            }

            return;
        }

        if (_view.RequestPixelCapture())
        {
            _captureRequested = true;
            Console.WriteLine($"[SELFTEST] 请求像素读回（帧 {_view.FrameIndex}，{_view.PixelWidth}×{_view.PixelHeight}）");
        }
    }

    private void OnPixelCaptured(FramePixelStats stats)
    {
        if (_finished)
        {
            return;
        }

        Console.WriteLine($"[SELFTEST] 像素统计：{stats.Describe()}");
        Console.WriteLine(
            $"[SELFTEST] 本帧提交：{_view.VisibleChunkCount}/{_view.TotalChunkCount} 块 · " +
            $"{_view.DrawnPointCount:N0} 点");
        SaveFramePng(stats);
        Console.WriteLine($"[SELFTEST] 渲染信息：{_view.Info?.Describe() ?? "(无)"}  FPS={_view.Fps:F1}");
        Console.WriteLine($"[SELFTEST] 耗时：总计 {_watch.Elapsed.TotalMilliseconds:F0} ms" +
                          $"（上传完成于 {_uploadCompleteAt.TotalMilliseconds:F0} ms）");

        if (_boundsOutOfView && !AllowPartialView)
        {
            Finish(false, "点云超出视野：Fit 没按视口宽高比框住数据（默认视角失效）");
            return;
        }

        if (_window.HasGrid && !_map.HasMap)
        {
            Finish(false, "包内有 PGM，但 2D 地图视图没有加载");
            return;
        }

        if (_window.HasGrid && !_view.GroundUploaded)
        {
            Finish(false, "包内有 PGM，但地面纹理没有上传到 GL（纹理路径回归）");
            return;
        }

        // 剔除只能"少画看不见的块"。全剔光说明视锥判定或分块包围盒算错了
        if (_view.TotalChunkCount > 0 && _view.VisibleChunkCount == 0)
        {
            Finish(false, $"分块剔除把 {_view.TotalChunkCount} 块全剔了（视锥判定或分块包围盒有误）");
            return;
        }

        if (stats.LooksRendered)
        {
            Finish(true, "画面有内容，且点云完整落在视野内");
            return;
        }

        string reason = stats.NonBackgroundRatio < 0.02
            ? $"画面几乎是空的，非背景像素仅 {stats.NonBackgroundRatio:P2}"
            : $"颜色过少（唯一色 {stats.DistinctColors}），着色器可能没生效";

        Finish(false, reason);
    }

    /// <summary>
    /// 把读回的帧存成 PNG。桌面截图抓不到 GL 渲染内容（拿到的是黑块），
    /// 这是唯一可靠的肉眼核对方式；路径可用 <c>PGV_SELFTEST_PNG</c> 覆盖。
    /// </summary>
    private static void SaveFramePng(FramePixelStats stats)
    {
        if (stats.Rgba is not { } rgba || stats.IsEmpty)
        {
            return;
        }

        string path = Environment.GetEnvironmentVariable("PGV_SELFTEST_PNG")
                      ?? Path.Combine(Path.GetTempPath(), "mypcd-selftest.png");

        try
        {
            int width = stats.Width;
            int height = stats.Height;
            int rowBytes = width * 4;

            var bitmap = new WriteableBitmap(
                new PixelSize(width, height),
                new Avalonia.Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Premul);

            byte[] row = new byte[rowBytes];

            using (ILockedFramebuffer buffer = bitmap.Lock())
            {
                for (int y = 0; y < height; y++)
                {
                    // GL 读回是自下而上的 RGBA：翻转行序，同时换成 BGRA
                    int sourceRow = (height - 1 - y) * rowBytes;
                    for (int x = 0; x < rowBytes; x += 4)
                    {
                        row[x] = rgba[sourceRow + x + 2];
                        row[x + 1] = rgba[sourceRow + x + 1];
                        row[x + 2] = rgba[sourceRow + x];
                        row[x + 3] = 255;
                    }

                    Marshal.Copy(row, 0, IntPtr.Add(buffer.Address, y * buffer.RowBytes), rowBytes);
                }
            }

            bitmap.Save(path, new PngBitmapEncoderOptions());
            Console.WriteLine($"[SELFTEST] 帧快照：{path}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SELFTEST] 帧快照失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private void Finish(bool pass, string message)
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        _poll.Stop();

        _view.CloudReady -= OnCloudReady;
        _view.PixelCaptured -= OnPixelCaptured;

        Environment.ExitCode = pass ? 0 : 1;
        Console.WriteLine($"[SELFTEST] 结果：{(pass ? "PASS" : "FAIL")} - {message}");

        // 必须走 lifetime.Shutdown：直接关窗口时 Avalonia 会把进程退出码重置为 0
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown(pass ? 0 : 1);
            return;
        }

        Dispatcher.UIThread.Post(_window.Close, DispatcherPriority.Background);
    }
}
