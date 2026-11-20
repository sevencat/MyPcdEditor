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

    /// <summary>当前相机（诊断与测试用）。</summary>
    public OrbitCamera Camera => _camera;

    public PointColorMode ColorMode { get; set; } = PointColorMode.Height;

    /// <summary>逻辑点大小（像素 @ 1x）。</summary>
    public float PointSize { get; set; } = 1.6f;

    /// <summary>是否把栅格图铺在地面上。</summary>
    public bool ShowGround { get; set; } = true;

    /// <summary>地面纹理整体透明度（1 = 按 occupancy 的 alpha 原样）。</summary>
    public float GroundOpacity { get; set; } = 1f;

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
        PumpGround();

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
            _activeCloud?.Dispose();
            _activeCloud = pending;
            _uploading = true;
            _uploadOffset = 0;
            UploadProgress = 0;
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
        _uploading = false;
        UploadProgress = 1;

        Console.WriteLine($"[GL] 点云上传完成：{cloud.PointCount:N0} 点 / {cloud.ByteLength / 1024 / 1024} MB");

        Dispatcher.UIThread.Post(() => CloudReady?.Invoke(cloud), DispatcherPriority.Background);
    }

    protected override void OnGlReleased()
    {
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
