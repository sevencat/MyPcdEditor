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

        lock (_sync)
        {
            _pendingCloud = cloud;
            _fitSamples = null;
            _uploadOffset = 0;
            _uploading = false;
            UploadProgress = 0;
        }

        RequestNextFrameRendering();
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
    private float CurrentAspect => PixelHeight > 0 ? PixelWidth / (float)PixelHeight : 1.6f;

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

    protected override void OnPointerDragged(double normalizedX, double normalizedY, double deltaX, double deltaY, MouseButton button)
    {
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
        }
    }
}
