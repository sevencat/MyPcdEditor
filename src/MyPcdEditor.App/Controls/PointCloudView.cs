using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Avalonia.Input;
using Avalonia.Threading;
using MyPcdEditor.App.Rendering;
using MyPcdEditor.Core.Grid;
using MyPcdEditor.Core.PointCloud;
using MyPcdEditor.Rendering.Gles3;
using Silk.NET.OpenGLES;

namespace MyPcdEditor.App.Controls;

/// <summary>
/// 点云 3D 视图。
/// </summary>
/// <remarks>
/// 上传在渲染回调里按片进行（每帧最多 <see cref="ChunkBytes"/>），
/// 389 MB 的点云不会让某一帧卡死；加载进度通过 <see cref="UploadProgress"/> 暴露给状态栏。
/// </remarks>
public sealed class PointCloudView : GlControlBase
{
    private const int ChunkBytes = 64 * 1024 * 1024;

    /// <summary>拟合相机时抽样的点数；太多没必要，太少会漏掉边缘。</summary>
    private const int FitSampleCount = 8192;

    // 清屏色同时作为自检判定的"背景"，两处必须一致
    private const float BackgroundR = 0.06f;
    private const float BackgroundG = 0.07f;
    private const float BackgroundB = 0.09f;

    private readonly OrbitCamera _camera = new();
    private readonly Lock _sync = new();

    private PointCloudRenderer? _renderer;
    private GroundTextureRenderer? _groundRenderer;
    private GroundTexture? _pendingGround;
    private bool _groundUpdatePending;
    private XyzCloud? _pendingCloud;
    private XyzCloud? _activeCloud;
    private long _uploadOffset;
    private bool _uploading;
    private bool _capturePending;
    private Vector3[]? _fitSamples;

    // 框选：按下点 → 拖动中的框 → 松开时交给 MainWindow 删点
    private (double X, double Y)? _selectionStart;
    private ScreenBox? _selection;
    private bool _selectionKeepInside;

    /// <summary>框小于这个像素尺寸当误点，不执行删除。</summary>
    private const double SelectionMinPixels = 4;

    // 笔刷：按住左键涂出一条折线，松手时整条折线交给 MainWindow 删点
    private readonly List<Vector2> _brushPath = [];
    private bool _brushing;
    private (double X, double Y)? _brushLast;

    /// <summary>笔迹最多这么多个顶点：绕圈涂太久时截断，否则松手后逐段判定会变慢。</summary>
    private const int BrushMaxVertices = 256;

    // 分块包围盒在后台线程扫（与 GL 上传并行）：_readyChunks 是算好待渲染线程取走的结果
    private CancellationTokenSource? _chunkBuildCts;
    private Task? _chunkBuildTask;
    private ChunkRange[]? _readyChunks;
    private long _chunkBoundsTicks;

    /// <summary>每块点数（<c>PGV_CHUNK_POINTS</c> 可覆盖，用于比较剔除粒度）。</summary>
    private static readonly int ChunkPoints =
        int.TryParse(Environment.GetEnvironmentVariable("PGV_CHUNK_POINTS"), out int chunkPoints)
        && chunkPoints > 0
            ? chunkPoints
            : ChunkBoundsBuilder.DefaultChunkPoints;

    /// <summary><c>PGV_CAM_ZOOM</c> 启动拉近倍数（1 = 不额外缩放，见 <see cref="ApplyStartupZoom"/>）。</summary>
    private static readonly float StartupZoom =
        float.TryParse(
            Environment.GetEnvironmentVariable("PGV_CAM_ZOOM"),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out float zoom) && zoom > 0 && MathF.Abs(zoom - 1f) > 1e-4f
            ? zoom
            : 1f;

    /// <summary>当前相机（诊断与测试用）。</summary>
    public OrbitCamera Camera => _camera;

    public PointColorMode ColorMode { get; set; } = PointColorMode.Height;

    /// <summary>逻辑点大小（像素 @ 1x）。</summary>
    public float PointSize { get; set; } = 1.6f;

    /// <summary>是否把栅格图铺在地面上。</summary>
    public bool ShowGround { get; set; } = true;

    /// <summary>
    /// 抽样步长（1 = 全量绘制，N = 每 N 个点画一个）。
    /// </summary>
    /// <remarks>
    /// 32 M 点全量只有 17 FPS，瓶颈在顶点提交（视口放大到 2380×1312 帧率不变，说明不是填充）。
    /// 索引抽样覆盖全点云，取 1/4 时 40 FPS、1/8 时 55 FPS。
    /// <c>PGV_DRAW_STEP</c> 只用于基准测试，正常走工具栏「细节」。
    /// </remarks>
    public int DrawStep { get; set; } =
        int.TryParse(Environment.GetEnvironmentVariable("PGV_DRAW_STEP"), out int step) && step > 0 ? step : 1;

    /// <summary>地面纹理整体透明度（1 = 按 occupancy 的 alpha 原样）。</summary>
    public float GroundOpacity { get; set; } = 1f;

    /// <summary>视锥剔除开关（<c>PGV_NO_CULL=1</c> 时默认关闭）。</summary>
    public bool CullingEnabled { get; set; } =
        Environment.GetEnvironmentVariable("PGV_NO_CULL") != "1";

    /// <summary>当前点云（只读借用：所有权仍在视图，换云后立即失效，不要缓存）。</summary>
    public XyzCloud? Cloud => _activeCloud;

    /// <summary>已提交、还没跑完上传的那份点云（换云后到渲染帧之间有效）。</summary>
    /// <remarks>
    /// 给「刚换完云就要读数据」的路径用（导出、无窗口测试）：这时 <see cref="Cloud"/> 还是旧的或空的。
    /// </remarks>
    internal XyzCloud? PendingCloud
    {
        get
        {
            lock (_sync)
            {
                return _pendingCloud;
            }
        }
    }

    /// <summary>最新的点云：优先还没上传的那份，没有再退回正在显示的。</summary>
    /// <remarks>
    /// 顺序不能反：换云之后、渲染帧之前，<see cref="Cloud"/> 还是**旧的那份**，
    /// 优先读它会让导出/优化拿到上一次的数据。
    /// </remarks>
    internal XyzCloud? CurrentCloud => PendingCloud ?? Cloud;

    /// <summary>已上传的点数（0 = 还没有点云）。</summary>
    public int PointCount => _renderer?.PointCount ?? 0;

    /// <summary>分块总数（0 = 还没拿到分块信息）。</summary>
    public int TotalChunkCount => _renderer?.TotalChunkCount ?? 0;

    /// <summary>上一帧通过视锥测试的块数。</summary>
    public int VisibleChunkCount => _renderer?.VisibleChunkCount ?? 0;

    /// <summary>上一帧实际提交的点数（抽样 + 剔除之后）。</summary>
    public int DrawnPointCount => _renderer?.DrawnPointCount ?? 0;

    /// <summary>地面纹理是否已上传（自检/诊断用）。</summary>
    public bool GroundUploaded => _groundRenderer is { HasTexture: true };

    /// <summary>已上传的地面纹理尺寸（未上传时为零）。</summary>
    public (int Width, int Height) GroundTextureSize =>
        _groundRenderer is null ? (0, 0) : (_groundRenderer.TextureWidth, _groundRenderer.TextureHeight);

    /// <summary>点云首帧渲染完成后触发（已在 UI 线程）。</summary>
    public event Action<XyzCloud>? CloudReady;

    /// <summary>框选模式：左键拖动画矩形（Shift + 拖 = 只保留框内），不再旋转视角。</summary>
    public bool BoxSelectMode { get; private set; }

    /// <summary>当前的框（拖动中持续更新），<c>null</c> = 没有框。</summary>
    public ScreenBox? Selection => _selection;

    /// <summary>框变化（拖动中 / 松开 / 清除）；UI 据此画矩形。</summary>
    public event Action<ScreenBox?>? SelectionChanged;

    /// <summary>框选完成：框 + 「只保留框内」标志（按住 Shift）。</summary>
    public event Action<ScreenBox, bool>? BoxSelected;

    /// <summary>框选模式被切换（工具栏按钮或 B 键）。</summary>
    public event Action<bool>? BoxSelectModeChanged;

    internal void SetBoxSelectMode(bool enabled)
    {
        if (BoxSelectMode == enabled)
        {
            return;
        }

        if (enabled)
        {
            // 两种编辑模式互斥：同时开着的话左键到底算画框还是画笔刷就说不清了
            SetBrushMode(false);
        }

        BoxSelectMode = enabled;
        if (!enabled)
        {
            ClearSelection();
        }

        BoxSelectModeChanged?.Invoke(enabled);
    }

    internal void ClearSelection()
    {
        _selectionStart = null;
        if (_selection is null)
        {
            return;
        }

        _selection = null;
        SelectionChanged?.Invoke(null);
    }

    /// <summary>笔刷模式：按住左键涂抹（拖动不转视角），松手删除涂到的点。</summary>
    public bool BrushMode { get; private set; }

    /// <summary>笔刷半径（像素）。</summary>
    public double BrushRadiusPixels { get; set; } = 32;

    /// <summary>笔迹变化（拖动中持续更新）：归一化顶点 + 归一化半径；UI 据此画粗折线。</summary>
    public event Action<IReadOnlyList<Vector2>, double>? BrushStrokeChanged;

    /// <summary>笔刷光标位置（画圆环），<c>null</c> = 隐藏。</summary>
    public event Action<Vector2?>? BrushPointerChanged;

    /// <summary>笔刷抬起：折线顶点 + 归一化半径，交给 MainWindow 删点。</summary>
    public event Action<Vector2[], double>? BrushCompleted;

    /// <summary>笔刷模式被切换（工具栏按钮或 E 键）。</summary>
    public event Action<bool>? BrushModeChanged;

    internal void SetBrushMode(bool enabled)
    {
        if (BrushMode == enabled)
        {
            return;
        }

        if (enabled)
        {
            SetBoxSelectMode(false);
        }

        BrushMode = enabled;
        if (!enabled)
        {
            ClearBrush();
            BrushPointerChanged?.Invoke(null);
        }

        BrushModeChanged?.Invoke(enabled);
    }

    internal void ClearBrush()
    {
        _brushing = false;
        _brushLast = null;
        if (_brushPath.Count > 0)
        {
            _brushPath.Clear();
            BrushStrokeChanged?.Invoke(_brushPath, CurrentBrushRadius);
        }
    }

    /// <summary>笔刷半径的归一化值（相对视口宽度）：Core 只认 0..1 坐标，不认识像素。</summary>
    private float CurrentBrushRadius => (float)(BrushRadiusPixels / Math.Max(Bounds.Width, 1));

    /// <summary>像素读回结果（在 UI 线程触发）。</summary>
    public event Action<FramePixelStats>? PixelCaptured;

    /// <summary>
    /// 请求在下一帧绘制完成后读回像素（自检/回归用）。
    /// 返回 <c>false</c> 表示当前还没有 GL 上下文，需稍后再试。
    /// </summary>
    public bool RequestPixelCapture()
    {
        if (_renderer is null || Info is null)
        {
            return false;
        }

        _capturePending = true;
        RequestNextFrameRendering();
        return true;
    }

    /// <summary>上传进度 0..1；没有点云时为 0。</summary>
    public double UploadProgress { get; private set; }

    public bool IsUploading => _uploading;

    /// <summary>换点云。接管 <paramref name="cloud"/> 的所有权（旧的会被释放）。</summary>
    public void SetCloud(XyzCloud cloud)
    {
        ArgumentNullException.ThrowIfNull(cloud);

        XyzCloud? dropped;
        lock (_sync)
        {
            // 换云时把还没上传的那份放掉：连着换两次（打开 → 合并）而中间没渲染帧时，
            // 被覆盖的旧云没人释放；零拷贝的话它还会一直占着 pcd 文件，替换原包就改不了名
            dropped = ReferenceEquals(_pendingCloud, cloud) ? null : _pendingCloud;
            _pendingCloud = cloud;
            _fitSamples = null;
            _uploadOffset = 0;
            _uploading = false;
            UploadProgress = 0;
        }

        // 拿到的引用已经不在字段里了，渲染线程要么取走的是新云，要么取走的是 null，不会撞上
        dropped?.Dispose();

        // 旧框/旧笔迹对应的是旧数据，换云后画面上已经没有可删的东西了
        ClearSelection();
        ClearBrush();
        RequestNextFrameRendering();
    }

    /// <summary>
    /// 把当前点云的所有权交给调用方（撤销栈用），视图此后不再释放它。
    /// 视图里此时没有点云（下一帧起画面为空），调用方随后应 <see cref="SetCloud"/> 换上新云。
    /// </summary>
    /// <remarks>
    /// 编辑类操作用得上：<see cref="SetCloud"/> 会在上传新云时释放旧云，
    /// 想在「撤销」时把旧云交回来，就必须先把所有权取走，不能指望它一直活着。
    /// </remarks>
    internal XyzCloud? DetachCloud()
    {
        XyzCloud? taken;
        lock (_sync)
        {
            taken = _pendingCloud ?? _activeCloud;
            _pendingCloud = null;

            if (ReferenceEquals(taken, _activeCloud))
            {
                // 正在显示的那份：停掉后台分块扫描，它读的就是这块映射内存
                CancelChunkBuild();
                _activeCloud = null;
            }

            _uploading = false;
            _uploadOffset = 0;
            _fitSamples = null;
        }

        // 框/笔迹对应的是被取走的数据，留着会画在空画面上
        ClearSelection();
        ClearBrush();
        RequestNextFrameRendering();
        return taken;
    }

    /// <summary>
    /// 设置地面纹理（<c>null</c> 表示清掉）。实际上传在下一帧渲染回调里做，
    /// 调用方可以在后台线程准备好 <see cref="GroundTexture"/>。
    /// </summary>
    public void SetGroundTexture(GroundTexture? texture)
    {
        lock (_sync)
        {
            _pendingGround = texture;
            _groundUpdatePending = true;
        }

        RequestNextFrameRendering();
    }

    /// <summary>当前视口宽高比（像素），还没渲染过时给个常见的宽屏比例。</summary>
    internal float CurrentAspect => PixelHeight > 0 ? PixelWidth / (float)PixelHeight : 1.6f;

    public void FitToCloud()
    {
        if (_activeCloud is not null)
        {
            FitCameraToCloud(_activeCloud);
        }
    }

    public void ResetCamera()
    {
        _camera.ResetOrientation();

        if (_activeCloud is not null)
        {
            FitCameraToCloud(_activeCloud);
        }
    }

    /// <summary>
    /// 按点云的实际分布拟合相机。用抽样点而不是包围盒角点，
    /// 否则斜置/细长的点云周围会留出大片空白（画面里只剩中间一小块）。
    /// </summary>
    private void FitCameraToCloud(XyzCloud cloud)
    {
        _fitSamples ??= cloud.SamplePoints(FitSampleCount);

        if (_fitSamples.Length > 0)
        {
            _camera.Fit(_fitSamples, cloud.Bounds.Center, CurrentAspect);
        }
        else
        {
            _camera.Fit(cloud.Bounds, CurrentAspect);
        }
    }

    protected override void OnGlReady()
    {
        _renderer = new PointCloudRenderer();
        _renderer.Initialize();

        _groundRenderer = new GroundTextureRenderer();
        _groundRenderer.Initialize();

        Console.WriteLine("[GL] 点云与地面渲染器就绪（着色器编译通过）");
    }

    protected override void OnFrame(double deltaSeconds, double elapsedSeconds)
    {
        GL gl = Gl;

        gl.ClearColor(BackgroundR, BackgroundG, BackgroundB, 1f);
        gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        if (_renderer is null)
        {
            return;
        }

        PumpUpload(_renderer);
        ApplyReadyChunks(_renderer);
        PumpGround();

        _renderer.SampleStep = DrawStep;
        _renderer.CullingEnabled = CullingEnabled;

        float aspect = PixelHeight > 0 ? PixelWidth / (float)PixelHeight : 1f;
        Matrix4x4 viewProjection = _camera.GetViewProjection(aspect);

        // 地面先画：它开深度写入，点云在其上方正常通过深度测试
        if (ShowGround && _groundRenderer is { HasTexture: true })
        {
            _groundRenderer.Draw(viewProjection, GroundOpacity);
        }

        if (_renderer is { HasCloud: true } && _activeCloud is not null)
        {
            _renderer.Draw(viewProjection, _camera, PixelHeight, ColorMode, PointSize);
        }

        if (_capturePending)
        {
            // 必须在这里读：FBO 仍是当前渲染目标，基类回调结束后就会解绑
            _capturePending = false;
            FramePixelStats stats = FramePixelStats.Read(
                PixelWidth,
                PixelHeight,
                (byte)MathF.Round(BackgroundR * 255f),
                (byte)MathF.Round(BackgroundG * 255f),
                (byte)MathF.Round(BackgroundB * 255f));

            if (!stats.IsEmpty)
            {
                Dispatcher.UIThread.Post(() => PixelCaptured?.Invoke(stats), DispatcherPriority.Background);
            }
        }
    }

    private void PumpUpload(PointCloudRenderer renderer)
    {
        // 新点云进来就重开一次上传；否则继续上传 _activeCloud 的剩余部分
        XyzCloud? pending;
        lock (_sync)
        {
            pending = _pendingCloud;
            _pendingCloud = null;
        }

        if (pending is not null)
        {
            // 先停掉旧云上的后台扫描：它读的是映射内存，Dispose 之后不能再有人扫
            CancelChunkBuild();

            _activeCloud?.Dispose();
            _activeCloud = pending;
            _uploading = true;
            _uploadOffset = 0;
            UploadProgress = 0;

            // 分块包围盒要扫全云（32 M 点 Debug 下约 1.4 s），放后台与 GL 上传并行；
            // 算完之前不剔除（chunks 为空就整块画），算完后才由 ApplyReadyChunks 接管。
            StartChunkBuild(pending);

            renderer.BeginUpload(pending.PointCount);
        }

        if (!_uploading || _activeCloud is null)
        {
            return;
        }

        XyzCloud cloud = _activeCloud;

        long remaining = cloud.ByteLength - _uploadOffset;
        if (remaining <= 0)
        {
            FinishUpload(renderer, cloud);
            return;
        }

        int length = (int)Math.Min(ChunkBytes, remaining);

        // 分片按点对齐（12 字节）：分块包围盒按点索引累加，半截点会让块索引对不上
        length -= length % XyzCloud.Stride;

        renderer.UploadChunk(cloud.GetBytes(_uploadOffset, length), _uploadOffset);

        _uploadOffset += length;
        UploadProgress = cloud.ByteLength == 0 ? 1 : (double)_uploadOffset / cloud.ByteLength;

        if (Environment.GetEnvironmentVariable("PGV_TRACE_FPS") == "1")
        {
            Console.WriteLine($"[GL] 上传分片：+{length / 1024 / 1024} MB → {_uploadOffset / 1024 / 1024}/{cloud.ByteLength / 1024 / 1024} MB");
        }

        if (_uploadOffset >= cloud.ByteLength)
        {
            FinishUpload(renderer, cloud);
        }
    }

    /// <summary>
    /// 地面纹理上传。等点云上传结束再传：地面高度取点云包围盒的最低点，
    /// 早传会用到还没写好的包围盒（默认 <c>(0,0)..(1,1)</c>），贴到错误高度。
    /// </summary>
    private void PumpGround()
    {
        GroundTexture? pending;

        lock (_sync)
        {
            if (!_groundUpdatePending)
            {
                return;
            }

            // 还没点云（或正在传）就等着：地面高度的参照是点云包围盒，
            // 先贴上去会用到默认的 (0,0)..(1,1) 包围盒，贴到 z=0 的错误位置。
            if (_activeCloud is null || _uploading)
            {
                return;
            }

            _groundUpdatePending = false;
            pending = _pendingGround;
            _pendingGround = null;
        }

        if (pending is null)
        {
            _groundRenderer?.Clear();
            return;
        }

        _groundRenderer!.Upload(pending, GroundZ());
    }

    /// <summary>地面高度：压在点云最低点下方一点，避免与地面点 z-fighting。</summary>
    private float GroundZ()
    {
        if (_activeCloud is null)
        {
            return 0f;
        }

        Bounds3 bounds = _activeCloud.Bounds;
        float range = MathF.Max(bounds.Max.Z - bounds.Min.Z, 0.01f);
        return bounds.Min.Z - MathF.Max(0.02f, range * 0.002f);
    }

    private void FinishUpload(PointCloudRenderer renderer, XyzCloud cloud)
    {
        renderer.EndUpload(cloud.Bounds);

        FitCameraToCloud(cloud);
        ApplyStartupZoom();

        _uploading = false;
        UploadProgress = 1;

        Console.WriteLine($"[GL] 点云上传完成：{cloud.PointCount:N0} 点 / {cloud.ByteLength / 1024 / 1024} MB");

        // 分块表可能还在后台算：到了就在后续帧里由 ApplyReadyChunks 接管，不必等它
        Dispatcher.UIThread.Post(() => CloudReady?.Invoke(cloud), DispatcherPriority.Background);
    }

    /// <summary>
    /// 后台扫描全云算分块包围盒。
    /// </summary>
    /// <remarks>
    /// 32 M 点 Debug 下约 1.4 s。塞进上传泵会给每片 64 MB 上传后加 200 ms 以上的停顿，
    /// 所以放后台线程跑，与 GL 上传并行；算完之前不剔除（chunks 为空就整块画）。
    /// </remarks>
    private void StartChunkBuild(XyzCloud cloud)
    {
        CancelChunkBuild();

        var cts = new CancellationTokenSource();
        int chunkPoints = ChunkPoints;
        long startTicks = Stopwatch.GetTimestamp();

        lock (_sync)
        {
            _chunkBuildCts = cts;

            // 在锁内起任务：任务写结果要拿同一把锁，不会出现"结果先于登记"的窗口
            _chunkBuildTask = Task.Run(() =>
            {
                ChunkRange[] chunks;
                try
                {
                    chunks = ChunkBoundsBuilder.Build(cloud, chunkPoints, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[GL] 分块包围盒计算失败：{ex.Message}");
                    return;
                }

                long elapsed = Stopwatch.GetTimestamp() - startTicks;

                lock (_sync)
                {
                    // 期间换了云或已经释放：结果作废，否则会把旧块表套到新点云上
                    if (!ReferenceEquals(_chunkBuildCts, cts))
                    {
                        return;
                    }

                    _readyChunks = chunks;
                    _chunkBoundsTicks = elapsed;
                }
            });
        }
    }

    /// <summary>
    /// 取消后台分块扫描并等它退出。
    /// </summary>
    /// <remarks>
    /// 扫描读的是点云的映射内存，<see cref="XyzCloud.Dispose"/> 之后不能还有人在读。
    /// 切片内部不检查取消（64 MB 一片最多几百毫秒），所以必须等，不能只发个取消就走。
    /// </remarks>
    private void CancelChunkBuild()
    {
        CancellationTokenSource? cts;
        Task? task;

        lock (_sync)
        {
            cts = _chunkBuildCts;
            task = _chunkBuildTask;
            _chunkBuildCts = null;
            _chunkBuildTask = null;
            _readyChunks = null;
        }

        if (cts is null)
        {
            return;
        }

        cts.Cancel();

        if (task is not null)
        {
            try
            {
                task.Wait(TimeSpan.FromSeconds(10));
            }
            catch (AggregateException)
            {
                // 任务内部已经打过日志
            }
        }

        // 超时说明任务可能还在用 token，此时不 Dispose（宁可留个 CTS，也不要放它去用已释放的 token）
        if (task is null || task.IsCompleted)
        {
            cts.Dispose();
        }
    }

    /// <summary>把后台算好的分块表交给渲染器（只能在渲染回调里调）。</summary>
    private void ApplyReadyChunks(PointCloudRenderer renderer)
    {
        ChunkRange[]? chunks;

        lock (_sync)
        {
            chunks = _readyChunks;
            if (chunks is null)
            {
                return;
            }

            _readyChunks = null;
        }

        renderer.SetChunks(chunks);

        double costMs = _chunkBoundsTicks * 1000.0 / Stopwatch.Frequency;
        Console.WriteLine($"[GL] 分块就绪：{chunks.Length} 块 · 后台扫描 {costMs:F0} ms");

        if (_activeCloud is not null)
        {
            PrintChunkLocality(chunks, _activeCloud.Bounds);
        }
    }

    /// <summary>
    /// 打印分块的空间紧凑度：块的对角线越接近全图，说明点顺序在空间上越分散，剔除能省的越少。
    /// </summary>
    private static void PrintChunkLocality(ChunkRange[] chunks, Bounds3 bounds)
    {
        if (chunks.Length == 0)
        {
            return;
        }

        double cloudDiagonal = bounds.Size.Length();
        double sum = 0;
        double max = 0;

        foreach (ChunkRange chunk in chunks)
        {
            double diagonal = chunk.Bounds.Size.Length();
            sum += diagonal;
            max = Math.Max(max, diagonal);
        }

        Console.WriteLine(
            $"[GL] 分块：{chunks.Length} 块 · 块对角线 平均 {sum / chunks.Length:F0} m / 最大 {max:F0} m" +
            $"（全图 {cloudDiagonal:F0} m）");
    }

    /// <summary>
    /// <c>PGV_CAM_ZOOM=0.2</c>：拟合后把相机拉近，用于测量"只看局部"时的剔除效果。
    /// 只影响启动那一次，按 F / R 仍是正常适配。
    /// </summary>
    private void ApplyStartupZoom()
    {
        if (StartupZoom != 1f)
        {
            _camera.Distance *= StartupZoom;
        }
    }

    protected override void OnGlReleased()
    {
        // 后台扫描读的是点云的映射内存，必须先停再释放
        CancelChunkBuild();

        _renderer?.Dispose();
        _renderer = null;

        _groundRenderer?.Dispose();
        _groundRenderer = null;

        _activeCloud?.Dispose();
        _activeCloud = null;
        _fitSamples = null;

        lock (_sync)
        {
            _pendingCloud?.Dispose();
            _pendingCloud = null;
            _pendingGround = null;
            _groundUpdatePending = false;
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        // 松开时 e 里拿不到按下那一刻的修饰键，所以先记下来
        _selectionKeepInside = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        base.OnPointerPressed(e);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_selectionStart is not null)
        {
            ClearSelection();
        }

        if (_brushing)
        {
            // 捕获丢了多半是窗口失焦：这一笔作废，不能拿半截笔迹去删点
            ClearBrush();
        }
    }

    protected override void OnPointerMoved(double normalizedX, double normalizedY)
    {
        if (BrushMode)
        {
            BrushPointerChanged?.Invoke(new Vector2((float)normalizedX, (float)normalizedY));
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (BrushMode)
        {
            BrushPointerChanged?.Invoke(null);
        }
    }

    protected override void OnPointerPressed(double normalizedX, double normalizedY, MouseButton button)
    {
        if (button != MouseButton.Left)
        {
            return;
        }

        if (BoxSelectMode)
        {
            _selectionStart = (normalizedX, normalizedY);
            _selection = ScreenBox.FromCorners(normalizedX, normalizedY, normalizedX, normalizedY);
            SelectionChanged?.Invoke(_selection);
            return;
        }

        if (BrushMode)
        {
            _brushing = true;
            _brushPath.Clear();
            _brushPath.Add(new Vector2((float)normalizedX, (float)normalizedY));
            _brushLast = (normalizedX, normalizedY);
            BrushStrokeChanged?.Invoke(_brushPath, CurrentBrushRadius);
        }
    }

    protected override void OnPointerDragged(double normalizedX, double normalizedY, double deltaX, double deltaY, MouseButton button)
    {
        if (_brushing)
        {
            AppendBrushPoint(normalizedX, normalizedY);
            return;
        }

        if (_selectionStart is { } start)
        {
            // 拖的是框，不是视角
            _selection = ScreenBox.FromCorners(start.X, start.Y, normalizedX, normalizedY);
            SelectionChanged?.Invoke(_selection);
            return;
        }

        double width = Math.Max(Bounds.Width, 1);
        double height = Math.Max(Bounds.Height, 1);

        if (button == MouseButton.Left)
        {
            _camera.Orbit((float)(deltaX / width), (float)(deltaY / height));
        }
        else
        {
            _camera.Pan((float)deltaX, (float)deltaY, PixelHeight);
        }
    }

    protected override void OnPointerReleased(double normalizedX, double normalizedY, MouseButton button)
    {
        if (_brushing)
        {
            AppendBrushPoint(normalizedX, normalizedY);
            _brushing = false;
            _brushLast = null;

            Vector2[] path = _brushPath.ToArray();
            _brushPath.Clear();

            // 先把手上的折线收起来，再去删点（删点是异步的，折线不该跟着停留）
            BrushStrokeChanged?.Invoke(_brushPath, CurrentBrushRadius);
            if (path.Length > 0)
            {
                BrushCompleted?.Invoke(path, CurrentBrushRadius);
            }

            return;
        }

        if (_selectionStart is not { } start)
        {
            return;
        }

        _selectionStart = null;
        ScreenBox box = ScreenBox.FromCorners(start.X, start.Y, normalizedX, normalizedY);

        // 几像素的抖动不算框：否则点一下就把脚下那一片删了
        if (box.Width * Bounds.Width < SelectionMinPixels || box.Height * Bounds.Height < SelectionMinPixels)
        {
            ClearSelection();
            return;
        }

        _selection = box;
        SelectionChanged?.Invoke(box);
        BoxSelected?.Invoke(box, _selectionKeepInside);
    }

    /// <summary>笔迹按「每半个笔刷」重采样：点太密松手后逐段判定慢，太稀折线的拐角会露。</summary>
    private void AppendBrushPoint(double normalizedX, double normalizedY)
    {
        if (_brushPath.Count >= BrushMaxVertices)
        {
            return;
        }

        double width = Math.Max(Bounds.Width, 1);
        double height = Math.Max(Bounds.Height, 1);

        if (_brushLast is { } last)
        {
            double step = Math.Max(CurrentBrushRadius * 0.5 * width, 1);
            double dx = (normalizedX - last.X) * width;
            double dy = (normalizedY - last.Y) * height;
            if ((dx * dx) + (dy * dy) < step * step)
            {
                return;
            }
        }

        _brushPath.Add(new Vector2((float)normalizedX, (float)normalizedY));
        _brushLast = (normalizedX, normalizedY);
        BrushStrokeChanged?.Invoke(_brushPath, CurrentBrushRadius);
    }

    protected override void OnScroll(double normalizedX, double normalizedY, double delta)
        => _camera.Zoom((float)delta);

    protected override void OnKeyChanged(Key key, bool down)
    {
        if (!down)
        {
            return;
        }

        switch (key)
        {
            case Key.F:
                FitToCloud();
                break;
            case Key.R:
                ResetCamera();
                break;
            case Key.B:
                SetBoxSelectMode(!BoxSelectMode);
                break;
            case Key.E:
                SetBrushMode(!BrushMode);
                break;
        }
    }
}
