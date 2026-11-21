using System.Globalization;
using System.Numerics;

namespace MyPcdEditor.Core.Merge;

/// <summary>
/// 平面刚体变换：绕 Z 轴的 yaw + 三维平移。
/// </summary>
/// <remarks>
/// <para>
/// 只用 4 个自由度（yaw + xyz）而不是完整 6DoF：两趟巡检出自同一台机器人、同一套重力对齐，
/// roll/pitch 的差异来自建图漂移而非真实姿态变化，放开它们只会让配准在弱约束方向（平坦地面）乱跑。
/// 真需要 6DoF 时再引入四元数，当前数据集用不上。
/// </para>
/// <para>
/// 语义：把「源坐标系」里的点搬到「目标坐标系」。</para>
/// </remarks>
public readonly record struct AlignmentTransform(float Yaw, Vector3 Translation)
{
    /// <summary>恒等变换。</summary>
    public static AlignmentTransform Identity => default;

    /// <summary>yaw 的度数表示（日志/界面用）。</summary>
    public float YawDegrees => Yaw * (180f / MathF.PI);

    /// <summary>只平移的变换。</summary>
    public static AlignmentTransform FromTranslation(Vector3 translation) => new(0f, translation);

    /// <summary>把源点变换到目标系。</summary>
    public Vector3 Apply(Vector3 point) => RotateY(point, Yaw) + Translation;

    /// <summary>绕原点（Z 轴）旋转，右手系、角度为弧度。</summary>
    public static Vector3 RotateY(Vector3 point, float yaw)
    {
        float cos = MathF.Cos(yaw);
        float sin = MathF.Sin(yaw);
        return new Vector3((point.X * cos) - (point.Y * sin), (point.X * sin) + (point.Y * cos), point.Z);
    }

    /// <summary>
    /// 组合：先应用 <c>this</c>，再应用 <paramref name="after"/>。
    /// </summary>
    /// <remarks>ICP 每轮求的是「对已变换点的增量」，所以要写成 <c>current.Then(delta)</c>。</remarks>
    public AlignmentTransform Then(AlignmentTransform after)
        => new(after.Yaw + Yaw, RotateY(Translation, after.Yaw) + after.Translation);

    /// <summary>逆变换（点云从目标系搬回源系时用）。</summary>
    public AlignmentTransform Inverse()
    {
        float inverseYaw = -Yaw;
        return new AlignmentTransform(inverseYaw, -RotateY(Translation, inverseYaw));
    }

    /// <summary>展开成 4×4 矩阵（交给渲染或外部工具核对）。</summary>
    public Matrix4x4 ToMatrix()
    {
        float cos = MathF.Cos(Yaw);
        float sin = MathF.Sin(Yaw);
        return new Matrix4x4(
            cos, sin, 0f, 0f,
            -sin, cos, 0f, 0f,
            0f, 0f, 1f, 0f,
            Translation.X, Translation.Y, Translation.Z, 1f);
    }

    /// <summary>与另一个变换的差异大小（平移米 + yaw 弧度），用于收敛判定。</summary>
    public (float Translation, float Yaw) Difference(AlignmentTransform other)
        => ((this.Translation - other.Translation).Length(), MathF.Abs(Yaw - other.Yaw));

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture,
            $"yaw={YawDegrees:F3}° t=({Translation.X:F3}, {Translation.Y:F3}, {Translation.Z:F3})");
}
