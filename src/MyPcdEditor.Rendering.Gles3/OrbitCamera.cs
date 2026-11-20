using System.Numerics;
using MyPcdEditor.Core.PointCloud;

namespace MyPcdEditor.Rendering.Gles3;

/// <summary>
/// 轨道相机：绕 <see cref="Target"/> 的球坐标（yaw/pitch/distance）。
/// </summary>
/// <remarks>点云坐标系是 SLAM 的 z-up，所以 up 向量取 <see cref="Vector3.UnitZ"/>。</remarks>
public sealed class OrbitCamera
{
    /// <summary>默认水平角：-45°，配合 <see cref="DefaultPitch"/> 形成"斜 45°"俯视。</summary>
    public const float DefaultYaw = -MathF.PI / 4f;

    /// <summary>默认俯仰角：45° 俯视。</summary>
    public const float DefaultPitch = MathF.PI / 4f;

    /// <summary>水平角可微调幅度（±25°）。</summary>
    public const float YawAllowance = 25f * MathF.PI / 180f;

    /// <summary>俯仰角可微调幅度（±20°）。</summary>
    public const float PitchAllowance = 20f * MathF.PI / 180f;

    private const float MinDistance = 0.05f;

    /// <summary>
    /// 拖动灵敏度（弧度 / 归一化屏幕尺寸）。取值与 <see cref="YawAllowance"/> 匹配：
    /// 拖满整个视口宽约等于走完允许范围，因此拖动只能"微调"。
    /// </summary>
    private const float OrbitSensitivity = 0.9f;

    public Vector3 Target { get; set; } = Vector3.Zero;

    public float Distance { get; set; } = 50f;

    /// <summary>水平角（弧度）。</summary>
    public float Yaw { get; set; } = DefaultYaw;

    /// <summary>俯仰角（弧度），正值从上方看向下。</summary>
    public float Pitch { get; set; } = DefaultPitch;

    /// <summary>水平角下限；默认限制在默认视角 -25°。</summary>
    public float MinYaw { get; set; } = DefaultYaw - YawAllowance;

    /// <summary>水平角上限；默认限制在默认视角 +25°。</summary>
    public float MaxYaw { get; set; } = DefaultYaw + YawAllowance;

    /// <summary>俯仰角下限；默认限制在默认视角 -20°。</summary>
    public float MinPitch { get; set; } = DefaultPitch - PitchAllowance;

    /// <summary>俯仰角上限；默认限制在默认视角 +20°。</summary>
    public float MaxPitch { get; set; } = DefaultPitch + PitchAllowance;

    public float FovDegrees { get; set; } = 55f;

    public float NearPlane { get; private set; } = 0.1f;

    public float FarPlane { get; private set; } = 10000f;

    /// <summary>从目标点指向相机的单位向量。</summary>
    public Vector3 DirectionToEye
    {
        get
        {
            float cosPitch = MathF.Cos(Pitch);
            return new Vector3(
                cosPitch * MathF.Cos(Yaw),
                cosPitch * MathF.Sin(Yaw),
                MathF.Sin(Pitch));
        }
    }

    public Vector3 Eye => Target + (DirectionToEye * Distance);

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

    /// <summary>
    /// 按包围盒把相机拉到"恰好能看全"的位置，目标点就是点云中心。
    /// </summary>
    /// <param name="aspectRatio">视口宽高比（像素宽 / 像素高），影响水平方向的可视范围。</param>
    public void Fit(Bounds3 bounds, float aspectRatio = 16f / 9f)
    {
        Span<Vector3> corners = stackalloc Vector3[8];
        for (int i = 0; i < 8; i++)
        {
            corners[i] = new Vector3(
                (i & 1) == 0 ? bounds.Min.X : bounds.Max.X,
                (i & 2) == 0 ? bounds.Min.Y : bounds.Max.Y,
                (i & 4) == 0 ? bounds.Min.Z : bounds.Max.Z);
        }

        Fit(corners, bounds.Center, aspectRatio);
    }

    /// <summary>
    /// 按一组代表点把相机拉到"恰好能看全"的位置，目标点用 <paramref name="center"/>。
    /// </summary>
    /// <param name="points">代表点（通常是从点云均匀抽样的结果）。</param>
    /// <param name="aspectRatio">视口宽高比（像素宽 / 像素高）。</param>
    /// <remarks>
    /// 不能用"球半径 / tan(fov/2)"的粗略估计：斜视时数据投影是不对称的，粗略估计会让数据偏出画面。
    /// 这里逐点求出"要让它落在视野内所需的最小距离"，取最大值。
    /// 用真实抽样点而不是包围盒角点，能避免画面里点云缩成中间一小块。
    /// </remarks>
    public void Fit(ReadOnlySpan<Vector3> points, Vector3 center, float aspectRatio = 16f / 9f)
    {
        float tanV = MathF.Tan(FovDegrees * MathF.PI / 360f);
        float tanH = tanV * MathF.Max(aspectRatio, 0.2f);

        Vector3 dirToEye = DirectionToEye;
        Vector3 forward = -dirToEye;
        Vector3 right = Vector3.Cross(Vector3.UnitZ, dirToEye);
        right = right.LengthSquared() > 1e-6f ? Vector3.Normalize(right) : Vector3.UnitX;
        Vector3 up = Vector3.Cross(right, forward);

        float distance = 0f;
        float radius = 0f;

        foreach (Vector3 point in points)
        {
            Vector3 rel = point - center;
            float u = MathF.Abs(Vector3.Dot(rel, right));
            float v = MathF.Abs(Vector3.Dot(rel, up));

            // 深度随距离线性增长：distance - dot(rel, dirToEye) 必须够容纳 u/tanH 与 v/tanV
            float needed = MathF.Max(u / tanH, v / tanV) + Vector3.Dot(rel, dirToEye);
            distance = MathF.Max(distance, needed);
            radius = MathF.Max(radius, rel.Length());
        }

        if (!(distance > 0f))
        {
            distance = 1f;
        }

        radius = MathF.Max(radius, 0.5f);

        Target = center;
        Distance = distance * 1.08f; // 留 8% 边距，避免贴边裁切
        NearPlane = MathF.Max(radius / 1000f, 0.01f);
        FarPlane = MathF.Max(radius * 20f, 100f);

        // 外部（如反序列化）可能塞进越界角度，这里收敛回默认附近
        Yaw = ClampAngle(Yaw, MinYaw, MaxYaw);
        Pitch = ClampAngle(Pitch, MinPitch, MaxPitch);
    }

    /// <summary>回到默认的斜 45° 视角（不动 target / distance）。</summary>
    public void ResetOrientation()
    {
        Yaw = ClampAngle(DefaultYaw, MinYaw, MaxYaw);
        Pitch = ClampAngle(DefaultPitch, MinPitch, MaxPitch);
    }

    /// <summary>鼠标拖动旋转（dx/dy 为归一化位移）；只会落在允许的微调范围内。</summary>
    public void Orbit(float dx, float dy)
    {
        Yaw = ClampAngle(Yaw - (dx * OrbitSensitivity), MinYaw, MaxYaw);
        Pitch = ClampAngle(Pitch + (dy * OrbitSensitivity), MinPitch, MaxPitch);
    }

    /// <summary>区间非法（min &gt; max）时视为不限制，避免 <see cref="Math.Clamp(float, float, float)"/> 抛异常。</summary>
    private static float ClampAngle(float value, float min, float max)
        => min <= max ? Math.Clamp(value, min, max) : value;

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
