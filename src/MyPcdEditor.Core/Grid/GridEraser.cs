using System.Numerics;

namespace MyPcdEditor.Core.Grid;

/// <summary>
/// 2D 栅格笔刷：沿折线把刷到的像素改成「未知」或「空闲」。
/// </summary>
/// <remarks>
/// <para>
/// 与 3D 笔刷是同一套几何（点到折线段的距离 ≤ 半径，两端圆头），只是坐标系从世界投影换成栅格像素：
/// 调用方把光标位置换算成连续的 (col, row)，半径也换算成像素，Core 不认识屏幕和缩放。
/// </para>
/// <para>
/// 擦除只写「未知」或「空闲」，不写「占用」：地图工具替用户断言"这里能走"是危险的默认值，
/// 想加墙请走别的路径（当前没有）。
/// </para>
/// </remarks>
public static class GridEraser
{
    /// <summary>map_server 惯例的未知灰度值（<c>negate: true</c> 时取反）。</summary>
    public const byte UnknownValue = 205;

    /// <summary>map_server 惯例的空闲灰度值。</summary>
    public const byte FreeValue = 254;

    /// <summary>map_server 惯例的占用灰度值（<c>negate: false</c>）。</summary>
    public const byte OccupiedValue = 0;

    /// <summary>
    /// 把 <paramref name="path"/>（像素空间折线，坐标是连续的 col/row）覆盖的像素改写成
    /// <paramref name="target"/> 语义，返回新图与改动像素数；输入图不变。
    /// </summary>
    /// <param name="radiusPixels">笔刷半径（栅格像素）。</param>
    public static GridMorphResult Erase(
        PgmImage source, MapYaml meta, ReadOnlySpan<Vector2> path, double radiusPixels,
        OccupancyCell target = OccupancyCell.Unknown)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(meta);

        if (target == OccupancyCell.Occupied)
        {
            throw new ArgumentOutOfRangeException(nameof(target), "这是橡皮擦：只能刷成未知或空闲。");
        }

        if (path.Length == 0)
        {
            throw new ArgumentException("笔迹为空：至少需要一个点。", nameof(path));
        }

        if (!(radiusPixels > 0) || !double.IsFinite(radiusPixels))
        {
            throw new ArgumentOutOfRangeException(nameof(radiusPixels), "笔刷半径必须是正的有限值。");
        }

        byte value = ValueFor(target, meta);
        int width = source.Width;
        int height = source.Height;
        byte[] pixels = source.Pixels.ToArray();

        double radiusSq = radiusPixels * radiusPixels;
        var changed = 0;

        // 单点笔迹当作零长线段：画出来是一个圆点
        int segments = Math.Max(path.Length - 1, 1);

        for (var s = 0; s < segments; s++)
        {
            Vector2 a = path[s];
            Vector2 b = path.Length == 1 ? a : path[s + 1];

            int colStart = Math.Max((int)Math.Floor(Math.Min(a.X, b.X) - radiusPixels), 0);
            int colEnd = Math.Min((int)Math.Ceiling(Math.Max(a.X, b.X) + radiusPixels), width - 1);
            int rowStart = Math.Max((int)Math.Floor(Math.Min(a.Y, b.Y) - radiusPixels), 0);
            int rowEnd = Math.Min((int)Math.Ceiling(Math.Max(a.Y, b.Y) + radiusPixels), height - 1);

            for (int row = rowStart; row <= rowEnd; row++)
            {
                int offset = row * width;

                for (int col = colStart; col <= colEnd; col++)
                {
                    if (SquaredDistanceToSegment(col, row, a, b) > radiusSq)
                    {
                        continue;
                    }

                    int index = offset + col;
                    if (pixels[index] == value)
                    {
                        continue;
                    }

                    pixels[index] = value;
                    changed++;
                }
            }
        }

        var image = new PgmImage
        {
            Width = width,
            Height = height,
            MaxValue = source.MaxValue,
            Pixels = pixels,
        };

        return new GridMorphResult(image, changed, RemovedIslands: 0);
    }

    /// <summary>
    /// 给定语义在 <paramref name="meta"/> 的阈值下应该写什么灰度值。
    /// </summary>
    /// <remarks>
    /// 首选 map_server 惯例值（未知 205 / 空闲 254 / 占用 0），<c>negate: true</c> 时取反；
    /// 阈值被改成极端值时首选值可能落到别的语义上，再按「离首选最近」逐个试。
    /// 三种语义在阈值上没有任何区间可表示时（<c>free_thresh ≥ occupied_thresh</c>）返回首选值。
    /// </remarks>
    public static byte ValueFor(OccupancyCell cell, MapYaml meta)
    {
        ArgumentNullException.ThrowIfNull(meta);

        byte preferred = cell switch
        {
            OccupancyCell.Free => meta.Negate ? (byte)1 : FreeValue,
            OccupancyCell.Occupied => meta.Negate ? (byte)254 : OccupiedValue,
            _ => meta.Negate ? (byte)(255 - UnknownValue) : UnknownValue,
        };

        if (OccupancyPalette.Classify(preferred, meta) == cell)
        {
            return preferred;
        }

        for (var delta = 1; delta < 256; delta++)
        {
            int down = preferred - delta;
            if (down >= 0 && OccupancyPalette.Classify((byte)down, meta) == cell)
            {
                return (byte)down;
            }

            int up = preferred + delta;
            if (up <= 255 && OccupancyPalette.Classify((byte)up, meta) == cell)
            {
                return (byte)up;
            }
        }

        return preferred;
    }

    /// <summary>点到线段的最短距离平方（<c>t</c> 夹在 [0,1]，天然包含两端圆头）。</summary>
    private static double SquaredDistanceToSegment(double px, double py, Vector2 a, Vector2 b)
    {
        double vx = b.X - a.X;
        double vy = b.Y - a.Y;
        double wx = px - a.X;
        double wy = py - a.Y;
        double lengthSq = (vx * vx) + (vy * vy);

        if (lengthSq <= double.Epsilon)
        {
            return (wx * wx) + (wy * wy);
        }

        double t = Math.Clamp(((wx * vx) + (wy * vy)) / lengthSq, 0, 1);
        double dx = wx - (t * vx);
        double dy = wy - (t * vy);
        return (dx * dx) + (dy * dy);
    }
}
