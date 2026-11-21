using System.Numerics;
using MyPcdEditor.Core.Grid;

namespace MyPcdEditor.Core.Merge;

/// <summary>参与合并的一张栅格图（含它在源局部系里的摆放）。</summary>
public sealed record GridMergeSource(PgmImage Map, MapYaml Meta, AlignmentTransform Transform, string Name = "");

/// <summary>栅格合成结果。</summary>
public sealed record GridMergeResult(PgmImage Map, MapYaml Meta)
{
    /// <summary>占用像素数（诊断用）。</summary>
    public int OccupiedCount { get; init; }

    public int FreeCount { get; init; }

    public int UnknownCount { get; init; }

    /// <summary>被两张以上图覆盖的像素数（重叠区大小）。</summary>
    public int OverlapCount { get; init; }
}

/// <summary>
/// 多张栅格图按变换合成一张，范围取并集。
/// </summary>
/// <remarks>
/// <para>
/// 像素优先级：<b>占用 &gt; 未知 &gt; 空闲</b>。占用优先是常识；未知压在空闲之上是因为
/// 「一张图说能走、另一张图没探测到」不等于能走，宁可让导航绕一下，也不能把未观测区域当通道（ADR-014 同理）。
/// </para>
/// <para>
/// 输出统一成 <c>negate=false</c>（0 = 占用、205 = 未知、255 = 空闲），否则下游导航栈会读反。
/// 采样走逆映射（输出像素 → 源像素最近邻），所以任何 yaw 都不会留空洞。
/// </para>
/// </remarks>
public static class GridMerger
{
    public static GridMergeResult Merge(
        IReadOnlyList<GridMergeSource> sources,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);

        if (sources.Count == 0)
        {
            throw new ArgumentException("至少需要一张栅格图。", nameof(sources));
        }

        double resolution = double.MaxValue;
        foreach (GridMergeSource source in sources)
        {
            if (!(source.Meta.Resolution > 0))
            {
                throw new ArgumentException($"{source.Name} 的 resolution 非法：{source.Meta.Resolution}。");
            }

            resolution = Math.Min(resolution, source.Meta.Resolution);
        }

        (double MinX, double MinY, double MaxX, double MaxY) extent = UnionExtent(sources);

        double spanX = Math.Max(extent.MaxX - extent.MinX, resolution);
        double spanY = Math.Max(extent.MaxY - extent.MinY, resolution);

        int width = Math.Max(1, (int)Math.Ceiling(spanX / resolution));
        int height = Math.Max(1, (int)Math.Ceiling(spanY / resolution));

        if ((long)width * height > int.MaxValue)
        {
            throw new InvalidOperationException(
                $"合并后栅格 {width}×{height} 超出像素上限，请先降低分辨率或裁剪各图。");
        }

        var state = new byte[width * height]; // 0 未覆盖 / 1 空闲 / 2 未知 / 3 占用
        var coverage = new byte[width * height]; // 1 表示该像素被至少两张图覆盖
        double minX = extent.MinX;
        double minY = extent.MinY;

        for (var s = 0; s < sources.Count; s++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GridMergeSource source = sources[s];
            Paint(source, state, coverage, width, height, resolution, minX, minY, cancellationToken);
            progress?.Report((s + 1.0) / sources.Count * 0.85);
        }

        var pixels = new byte[width * height];
        var occupied = 0;
        var free = 0;
        var unknown = 0;
        var overlap = 0;

        for (var i = 0; i < pixels.Length; i++)
        {
            switch (state[i])
            {
                case 3:
                    pixels[i] = 0;
                    occupied++;
                    break;

                case 2:
                case 0:
                    pixels[i] = GridMorphology.UnknownValue;
                    unknown++;
                    break;

                default:
                    pixels[i] = 255;
                    free++;
                    break;
            }

            if (coverage[i] > 1)
            {
                overlap++;
            }
        }

        progress?.Report(1);

        return new GridMergeResult(
            new PgmImage { Width = width, Height = height, MaxValue = 255, Pixels = pixels },
            new MapYaml
            {
                Image = null,
                Resolution = resolution,
                OriginX = minX,
                OriginY = minY,
                OriginYaw = 0,
                Negate = false,
                OccupiedThresh = sources[0].Meta.OccupiedThresh,
                FreeThresh = sources[0].Meta.FreeThresh,
            })
        {
            OccupiedCount = occupied,
            FreeCount = free,
            UnknownCount = unknown,
            OverlapCount = overlap,
        };
    }

    /// <summary>各图（在源局部系里是轴对齐矩形）经变换后的四角并集。</summary>
    private static (double MinX, double MinY, double MaxX, double MaxY) UnionExtent(IReadOnlyList<GridMergeSource> sources)
    {
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;

        foreach (GridMergeSource source in sources)
        {
            (double x0, double y0, double x1, double y1) = TransformedExtent(source);
            minX = Math.Min(minX, x0);
            minY = Math.Min(minY, y0);
            maxX = Math.Max(maxX, x1);
            maxY = Math.Max(maxY, y1);
        }

        return (minX, minY, maxX, maxY);
    }

    /// <summary>图经变换后在参考系里的轴对齐包围盒（旋转时取四角的 bbox）。</summary>
    private static (double X0, double Y0, double X1, double Y1) TransformedExtent(GridMergeSource source)
    {
        (double x0, double y0, double x1, double y1) = LocalExtent(source);

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;

        foreach ((double x, double y) in new[] { (x0, y0), (x1, y0), (x0, y1), (x1, y1) })
        {
            Vector3 p = source.Transform.Apply(new Vector3((float)x, (float)y, 0f));
            minX = Math.Min(minX, p.X);
            minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X);
            maxY = Math.Max(maxY, p.Y);
        }

        return (minX, minY, maxX, maxY);
    }

    /// <summary>图在源局部系里的覆盖范围（yaml origin 就是左下角）。</summary>
    private static (double X0, double Y0, double X1, double Y1) LocalExtent(GridMergeSource source)
    {
        double x0 = source.Meta.OriginX;
        double y0 = source.Meta.OriginY;
        double x1 = x0 + (source.Map.Width * source.Meta.Resolution);
        double y1 = y0 + (source.Map.Height * source.Meta.Resolution);
        return (x0, y0, x1, y1);
    }

    private static void Paint(
        GridMergeSource source, byte[] state, byte[] coverage,
        int width, int height, double resolution, double minX, double minY,
        CancellationToken cancellationToken)
    {
        PgmImage map = source.Map;
        MapYaml meta = source.Meta;
        AlignmentTransform inverse = source.Transform.Inverse();

        // 源的覆盖范围（已变换到参考系）换成输出像素范围：只遍历这个窗口，不扫全图
        (double x0, double y0, double x1, double y1) = TransformedExtent(source);

        double c0 = (x0 - minX) / resolution;
        double c1 = (x1 - minX) / resolution;
        double r0 = height - ((y1 - minY) / resolution);
        double r1 = height - ((y0 - minY) / resolution);

        int colFrom = Math.Max(0, (int)Math.Floor(c0));
        int colTo = Math.Min(width, (int)Math.Ceiling(c1));
        int rowFrom = Math.Max(0, (int)Math.Floor(r0));
        int rowTo = Math.Min(height, (int)Math.Ceiling(r1));

        for (var row = rowFrom; row < rowTo; row++)
        {
            if ((row & 0x3F) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            double worldY = minY + ((height - row - 0.5) * resolution);

            for (var col = colFrom; col < colTo; col++)
            {
                double worldX = minX + ((col + 0.5) * resolution);
                Vector3 p = inverse.Apply(new Vector3((float)worldX, (float)worldY, 0f));

                (int sourceCol, int sourceRow) = meta.WorldToPixel(p.X, p.Y, map.Height);
                if (sourceCol < 0 || sourceCol >= map.Width || sourceRow < 0 || sourceRow >= map.Height)
                {
                    continue;
                }

                byte rank = OccupancyPalette.Classify(map[sourceCol, sourceRow], meta) switch
                {
                    OccupancyCell.Occupied => (byte)3,
                    OccupancyCell.Unknown => (byte)2,
                    _ => (byte)1,
                };

                int index = (row * width) + col;
                if (rank > state[index])
                {
                    state[index] = rank;
                }

                if (coverage[index] == 0)
                {
                    coverage[index] = 1;
                }
                else
                {
                    coverage[index] = 2;
                }
            }
        }
    }
}
