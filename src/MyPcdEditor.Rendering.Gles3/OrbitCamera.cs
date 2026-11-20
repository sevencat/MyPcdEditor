using System.Numerics;
using MyPcdEditor.Core.PointCloud;

namespace MyPcdEditor.Rendering.Gles3;

/// <summary>
/// 轨道相机：绕 <see cref="Target"/> 的球坐标（yaw/pitch/distance）。
/// </summary>
/// <remarks>点云坐标系是 SLAM 的 z-up，所以 up 向量取 <see cref="Vector3.UnitZ"/>。</remarks>
public sealed class OrbitCamera
{
    private const float MinDistance = 0.05f;
    private const float MaxPitch = 1.5f;

    public Vector3 Target { get; set; } = Vector3.Zero;

    public float Distance { get; set; } = 50f;

    /// <summary>水平角（弧度）。</summary>
    public float Yaw { get; set; } = -2.2f;

    /// <summary>俯仰角（弧度），正值从上方看向下。</summary>
    public float Pitch { get; set; } = 0.55f;

    public float FovDegrees { get; set; } = 55f;

    public float NearPlane { get; private set; } = 0.1f;

    public float FarPlane { get; private set; } = 10000f;

    public Vector3 Eye
    {
        get
        {
            float cosPitch = MathF.Cos(Pitch);
            var offset = new Vector3(
                cosPitch * MathF.Cos(Yaw),
                cosPitch * MathF.Sin(Yaw),
                MathF.Sin(Pitch));
            return Target + (offset * Distance);
        }
    }

    public Matrix4x4 View => Matrix4x4.CreateLookAt(Eye, Target, Vector3.UnitZ);

    /// <summary>每像素对应的投影尺度（<c>gl_PointSize = uPointSize * scale / w</c>）。</summary>
    public float PixelScale(int viewportHeight)
        => (float)(viewportHeight / (2.0 * Math.Tan(FovDegrees * Math.PI / 360.0)));

    public Matrix4x4 GetProjection(float aspectRatio)
    {
        float aspect = aspectRatio > 0 ? aspectRatio : 1f;
        return Matrix4x4.CreatePerspectiveFieldOfView(
            FovDegrees * MathF.PI / 180f, aspect, NearPlane, FarPlane);
    }

    public Matrix4x4 GetViewProjection(float aspectRatio) => View * GetProjection(aspectRatio);

    /// <summary>按包围盒把相机拉到能看全的位置。</summary>
    public void Fit(Bounds3 bounds)
    {
        Vector3 size = bounds.Size;
        float radius = MathF.Max(size.Length() * 0.5f, 0.5f);

        Target = bounds.Center;
        Distance = radius / MathF.Tan(FovDegrees * MathF.PI / 360f) * 1.15f;
        NearPlane = MathF.Max(radius / 1000f, 0.01f);
        FarPlane = MathF.Max(radius * 20f, 100f);
    }

    /// <summary>鼠标拖动旋转（dx/dy 为归一化位移）。</summary>
    public void Orbit(float dx, float dy)
    {
        Yaw -= dx * 3.5f;
        Pitch = Math.Clamp(Pitch + (dy * 3.5f), -MaxPitch, MaxPitch);
    }

    /// <summary>平移：按屏幕方向移动目标点。</summary>
    public void Pan(float dx, float dy, int viewportHeight)
    {
        Vector3 forward = Vector3.Normalize(Target - Eye);
        Vector3 right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitZ));
        if (!float.IsFinite(right.X))
        {
            right = Vector3.UnitX;
        }

        Vector3 up = Vector3.Cross(right, forward);
        float worldPerPixel = 2f * Distance * MathF.Tan(FovDegrees * MathF.PI / 360f) / MathF.Max(viewportHeight, 1);
        Target += ((-right * dx) + (up * dy)) * worldPerPixel;
    }

    /// <summary>滚轮缩放（delta &gt; 0 拉近）。</summary>
    public void Zoom(float delta)
    {
        float factor = MathF.Pow(0.85f, delta);
        Distance = Math.Clamp(Distance * factor, MinDistance, 1e7f);
    }
}
