using System.Numerics;
using MyPcdEditor.Core.PointCloud;

namespace MyPcdEditor.Core.Geometry;

/// <summary>
/// 视锥（6 个平面），只做"包围盒完全在外就跳过"这一档粗剔除。
/// </summary>
/// <remarks>
/// <para>
/// 平面从 view-projection 矩阵直接取（Gribb–Hartmann）。矩阵约定是 <b>System.Numerics 的行向量</b>
/// 形式（<c>v' = v * M</c>），所以裁剪坐标是"顶点与矩阵的列做点积"：
/// <c>clip.x = v · col0</c>。GL 的 NDC 是 <c>-w ≤ x,y,z ≤ w</c>，于是：
/// left = col3 + col0，right = col3 - col0，bottom/top 同理用 col1，
/// near = col3 + col2，far = col3 - col2。
/// </para>
/// <para>
/// 剔除是保守的：点到平面的距离只取包围盒上"最靠近平面正向"的那个角（p-vertex），
/// 该角都在外侧才判为不可见，因此不会剔掉任何真正可见的点。
/// </para>
/// </remarks>
public readonly struct Frustum
{
    private readonly Plane _left;
    private readonly Plane _right;
    private readonly Plane _bottom;
    private readonly Plane _top;
    private readonly Plane _near;
    private readonly Plane _far;

    private Frustum(Plane left, Plane right, Plane bottom, Plane top, Plane near, Plane far)
    {
        _left = left;
        _right = right;
        _bottom = bottom;
        _top = top;
        _near = near;
        _far = far;
    }

    /// <summary>从 view-projection 矩阵构造（行向量约定，见类型注释）。</summary>
    public static Frustum FromViewProjection(in Matrix4x4 viewProjection)
    {
        var column0 = new Vector4(viewProjection.M11, viewProjection.M21, viewProjection.M31, viewProjection.M41);
        var column1 = new Vector4(viewProjection.M12, viewProjection.M22, viewProjection.M32, viewProjection.M42);
        var column2 = new Vector4(viewProjection.M13, viewProjection.M23, viewProjection.M33, viewProjection.M43);
        var column3 = new Vector4(viewProjection.M14, viewProjection.M24, viewProjection.M34, viewProjection.M44);

        return new Frustum(
            ToPlane(column3 + column0),
            ToPlane(column3 - column0),
            ToPlane(column3 + column1),
            ToPlane(column3 - column1),
            ToPlane(column3 + column2),
            ToPlane(column3 - column2));
    }

    /// <summary>包围盒与视锥是否有交集（保守判断：宁可多画，不可漏画）。</summary>
    public bool Intersects(in Bounds3 bounds) =>
        IsInside(_left, bounds)
        && IsInside(_right, bounds)
        && IsInside(_bottom, bounds)
        && IsInside(_top, bounds)
        && IsInside(_near, bounds)
        && IsInside(_far, bounds);

    /// <summary>单个点是否落在视锥内（测试与诊断用）。</summary>
    public bool Contains(Vector3 point) =>
        Distance(_left, point) >= 0
        && Distance(_right, point) >= 0
        && Distance(_bottom, point) >= 0
        && Distance(_top, point) >= 0
        && Distance(_near, point) >= 0
        && Distance(_far, point) >= 0;

    private static bool IsInside(in Plane plane, in Bounds3 bounds)
    {
        // p-vertex：沿平面法线最远的那个角。它在平面外侧时整盒都在外侧
        var corner = new Vector3(
            plane.Normal.X >= 0 ? bounds.Max.X : bounds.Min.X,
            plane.Normal.Y >= 0 ? bounds.Max.Y : bounds.Min.Y,
            plane.Normal.Z >= 0 ? bounds.Max.Z : bounds.Min.Z);

        return Distance(plane, corner) >= 0;
    }

    private static float Distance(in Plane plane, Vector3 point) =>
        Vector3.Dot(plane.Normal, point) + plane.D;

    private static Plane ToPlane(Vector4 value)
    {
        float length = new Vector3(value.X, value.Y, value.Z).Length();

        // 退化平面（法线为 0）：当作"永远可见"，让剔除自动失效而不是误杀
        return length > 1e-6f
            ? new Plane(value.X / length, value.Y / length, value.Z / length, value.W / length)
            : new Plane(Vector3.Zero, 1f);
    }
}
