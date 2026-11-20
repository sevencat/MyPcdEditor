using System.Numerics;
using Avalonia.Input;
using Avalonia.Threading;
using MyPcdEditor.App.Rendering;
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

    private readonly OrbitCamera _camera = new();
    private readonly Lock _sync = new();

    private PointCloudRenderer? _renderer;
    private XyzCloud? _pendingCloud;
    private XyzCloud? _activeCloud;
    private long _uploadOffset;
    private bool _uploading;

    public PointColorMode ColorMode { get; set; } = PointColorMode.Height;

    /// <summary>逻辑点大小（像素 @ 1x）。</summary>
    public float PointSize { get; set; } = 1.6f;

    /// <summary>点云首帧渲染完成后触发（已在 UI 线程）。</summary>
    public event Action<XyzCloud>? CloudReady;

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
            _uploadOffset = 0;
            _uploading = false;
            UploadProgress = 0;
        }

        RequestNextFrameRendering();
    }

    public void FitToCloud()
    {
        if (_activeCloud is not null)
        {
            _camera.Fit(_activeCloud.Bounds);
        }
    }

    public void ResetCamera()
    {
        _camera.Yaw = -2.2f;
        _camera.Pitch = 0.55f;

        if (_activeCloud is not null)
        {
            _camera.Fit(_activeCloud.Bounds);
        }
    }

    protected override void OnGlReady()
    {
        _renderer = new PointCloudRenderer();
        _renderer.Initialize();
        Console.WriteLine("[GL] 点云渲染器就绪（着色器编译通过）");
    }

    protected override void OnFrame(double deltaSeconds, double elapsedSeconds)
    {
        GL gl = Gl;

        gl.ClearColor(0.06f, 0.07f, 0.09f, 1f);
        gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        if (_renderer is null)
        {
            return;
        }

        PumpUpload(_renderer);

        if (_renderer is { HasCloud: true } && _activeCloud is not null)
        {
            float aspect = PixelHeight > 0 ? PixelWidth / (float)PixelHeight : 1f;
            Matrix4x4 viewProjection = _camera.GetViewProjection(aspect);
            _renderer.Draw(viewProjection, _camera, PixelHeight, ColorMode, PointSize);
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

    private void FinishUpload(PointCloudRenderer renderer, XyzCloud cloud)
    {
        renderer.EndUpload(cloud.Bounds);
        _camera.Fit(cloud.Bounds);
        _uploading = false;
        UploadProgress = 1;

        Console.WriteLine($"[GL] 点云上传完成：{cloud.PointCount:N0} 点 / {cloud.ByteLength / 1024 / 1024} MB");

        Dispatcher.UIThread.Post(() => CloudReady?.Invoke(cloud), DispatcherPriority.Background);
    }

    protected override void OnGlReleased()
    {
        _renderer?.Dispose();
        _renderer = null;

        _activeCloud?.Dispose();
        _activeCloud = null;

        lock (_sync)
        {
            _pendingCloud?.Dispose();
            _pendingCloud = null;
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
