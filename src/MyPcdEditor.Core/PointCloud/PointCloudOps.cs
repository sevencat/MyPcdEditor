using System.Numerics;

namespace MyPcdEditor.Core.PointCloud;

/// <summary>
/// 点云算子：都不修改输入云，返回新的 <see cref="XyzCloud"/>，可在后台线程调用。
/// </summary>
/// <remarks>
/// <para>
/// 输出云与源文件解耦：源云通常是零拷贝映射的（ADR-005），算子结果必须能脱离原文件存活与导出，
/// 所以一律产出托管数组（<see cref="XyzCloud.Create"/>）。
/// </para>
/// <para>32 M 点数大，长循环都定期检查取消并上报进度。</para>
/// </remarks>
public static class PointCloudOps
{
    private const int ProgressStride = 0xFFFFF;

    /// <summary>包围盒裁剪：保留（<paramref name="keepInside"/>）或剔除盒内的点。</summary>
    public static XyzCloud CropBox(
        XyzCloud cloud, Bounds3 box, bool keepInside = true,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cloud);

        int count = cloud.PointCount;
        if (count == 0)
        {
            return XyzCloud.Create([], 0, cloud.SourcePath);
        }

        ReadOnlySpan<byte> bytes = cloud.GetBytes(0, checked((int)cloud.ByteLength));

        int kept = 0;
        for (var i = 0; i < count; i++)
        {
            if ((i & ProgressStride) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report((double)i / count * 0.5);
            }

            Vector3 p = VoxelHash.ReadPoint(bytes, i);
            if (VoxelHash.IsFinite(p) && IsInside(p, box) == keepInside)
            {
                kept++;
            }
        }

        var data = new byte[kept * XyzCloud.Stride];
        var write = 0;
        for (var i = 0; i < count; i++)
        {
            if ((i & ProgressStride) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(0.5 + ((double)i / count * 0.5));
            }

            Vector3 p = VoxelHash.ReadPoint(bytes, i);
            if (VoxelHash.IsFinite(p) && IsInside(p, box) == keepInside)
            {
                VoxelHash.WritePoint(data, write++, p);
            }
        }

        progress?.Report(1);
        return XyzCloud.Create(data, write, cloud.SourcePath);
    }

    /// <summary>并行标记的分块点数（1 M 点 = 12 MB，48 M 点分 48 块）。</summary>
    private const int ScreenMarkChunk = 1 << 20;

    /// <summary>
    /// 屏幕矩形框选：删掉（<paramref name="keepInside"/> = false，默认）或只保留框内的点。
    /// </summary>
    /// <param name="viewProjection">相机的 view × projection，必须与渲染用的是同一个矩阵。</param>
    /// <param name="box">归一化矩形（0..1，左上原点）。</param>
    /// <remarks>
    /// 屏幕矩形在世界空间里是一个四棱锥而不是 AABB，所以只能逐点投影判断，不能退化成
    /// <see cref="CropBox"/>(盒子裁不掉「远处的黑点」这类目标)。相机后方（w ≤ 0）与 NaN/Inf 点
    /// 一律不算框内、且永远会被丢弃。48 M 点：并行标记 + 单线程拷贝，8 核约 0.5 s。
    /// </remarks>
    public static XyzCloud CropScreenBox(
        XyzCloud cloud, in Matrix4x4 viewProjection, ScreenBox box, bool keepInside = false,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cloud);

        if (box.IsEmpty)
        {
            throw new ArgumentException("选区为空：宽或高为 0 的框不构成选区。", nameof(box));
        }

        int count = cloud.PointCount;
        if (count == 0)
        {
            return XyzCloud.Create([], 0, cloud.SourcePath);
        }

        // 归一化框 → NDC：屏幕 y 向下，NDC y 向上
        float minX = (box.MinU * 2f) - 1f;
        float maxX = (box.MaxU * 2f) - 1f;
        float minY = 1f - (box.MaxV * 2f);
        float maxY = 1f - (box.MinV * 2f);

        // in 参数不能进 lambda，拷一份给并行块用（64 字节，可忽略）
        Matrix4x4 matrix = viewProjection;

        var keep = new byte[count];
        int chunks = (count + ScreenMarkChunk - 1) / ScreenMarkChunk;
        var marked = 0;

        Parallel.For(
            0,
            chunks,
            new ParallelOptions { CancellationToken = cancellationToken },
            chunk =>
            {
                int start = chunk * ScreenMarkChunk;
                int end = Math.Min(start + ScreenMarkChunk, count);
                ReadOnlySpan<byte> bytes = cloud.GetBytes(
                    (long)start * XyzCloud.Stride, (end - start) * XyzCloud.Stride);

                for (var i = 0; i < end - start; i++)
                {
                    Vector3 p = VoxelHash.ReadPoint(bytes, i);
                    keep[start + i] = !VoxelHash.IsFinite(p)
                        ? (byte)0
                        : (byte)(IsInsideScreen(p, matrix, minX, maxX, minY, maxY) == keepInside ? 1 : 0);
                }

                int done = Interlocked.Add(ref marked, end - start);
                progress?.Report((double)done / count * 0.8);
            });

        cancellationToken.ThrowIfCancellationRequested();

        var kept = 0;
        for (var i = 0; i < count; i++)
        {
            kept += keep[i];
        }

        return Compact(cloud, keep, kept, progress, cancellationToken);
    }

    /// <summary>
    /// 屏幕笔刷：删掉被笔迹（折线，两端圆头）覆盖的点。
    /// </summary>
    /// <param name="viewProjection">相机的 view × projection，必须与渲染用的是同一个矩阵。</param>
    /// <param name="centers">笔迹顶点，归一化 0..1（左上原点），相邻两点连成一段。</param>
    /// <param name="radius">笔刷半径，归一化（相对视口宽度）。</param>
    /// <param name="aspect">视口宽高比 W/H：v 方向要折算到 u 的尺度，圆在像素空间才是圆。</param>
    /// <remarks>
    /// 判定按「点到折线段的距离 ≤ 半径」做，与 UI 上画的粗折线是同一个几何，所见即所删。
    /// 相机后方（w ≤ 0）与 NaN/Inf 点一律丢弃。48 M 点、几十段笔迹：8 核约 0.5~1 s。
    /// </remarks>
    public static XyzCloud CropScreenBrush(
        XyzCloud cloud, in Matrix4x4 viewProjection, Vector2[] centers, float radius, float aspect,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cloud);
        ArgumentNullException.ThrowIfNull(centers);

        if (centers.Length == 0)
        {
            throw new ArgumentException("笔迹为空：至少需要一个圆心。", nameof(centers));
        }

        if (!(radius > 0f) || !float.IsFinite(radius))
        {
            throw new ArgumentOutOfRangeException(nameof(radius), "笔刷半径必须是正的有限值。");
        }

        if (!(aspect > 0f) || !float.IsFinite(aspect))
        {
            throw new ArgumentOutOfRangeException(nameof(aspect), "宽高比必须是正的有限值。");
        }

        int count = cloud.PointCount;
        if (count == 0)
        {
            return XyzCloud.Create([], 0, cloud.SourcePath);
        }

        float radiusSq = radius * radius;
        float invAspect = 1f / aspect;

        // 笔迹包围盒（v 方向按像素比例展开）：盒外的点跳过逐段距离计算
        float minU = float.MaxValue, maxU = float.MinValue, minV = float.MaxValue, maxV = float.MinValue;
        foreach (Vector2 c in centers)
        {
            minU = MathF.Min(minU, c.X);
            maxU = MathF.Max(maxU, c.X);
            minV = MathF.Min(minV, c.Y);
            maxV = MathF.Max(maxV, c.Y);
        }

        minU -= radius;
        maxU += radius;
        float radiusV = radius * aspect;
        minV -= radiusV;
        maxV += radiusV;

        // in 参数不能进 lambda，拷一份给并行块用（64 字节，可忽略）
        Matrix4x4 matrix = viewProjection;

        var keep = new byte[count];
        int chunks = (count + ScreenMarkChunk - 1) / ScreenMarkChunk;
        var marked = 0;

        Parallel.For(
            0,
            chunks,
            new ParallelOptions { CancellationToken = cancellationToken },
            chunk =>
            {
                int start = chunk * ScreenMarkChunk;
                int end = Math.Min(start + ScreenMarkChunk, count);
                ReadOnlySpan<byte> bytes = cloud.GetBytes(
                    (long)start * XyzCloud.Stride, (end - start) * XyzCloud.Stride);
                ReadOnlySpan<Vector2> path = centers;

                for (var i = 0; i < end - start; i++)
                {
                    Vector3 p = VoxelHash.ReadPoint(bytes, i);
                    keep[start + i] = !VoxelHash.IsFinite(p)
                        ? (byte)0
                        : (byte)(IsInsideBrush(p, matrix, path, radiusSq, invAspect, minU, maxU, minV, maxV) ? 0 : 1);
                }

                int done = Interlocked.Add(ref marked, end - start);
                progress?.Report((double)done / count * 0.8);
            });

        cancellationToken.ThrowIfCancellationRequested();

        var kept = 0;
        for (var i = 0; i < count; i++)
        {
            kept += keep[i];
        }

        return Compact(cloud, keep, kept, progress, cancellationToken);
    }

    /// <summary>按保留掩码紧凑打包（保持原始顺序）；<paramref name="kept"/> 是掩码里 1 的个数。</summary>
    private static XyzCloud Compact(
        XyzCloud cloud, byte[] keep, int kept,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        int count = cloud.PointCount;
        if (kept == 0)
        {
            progress?.Report(1);
            return XyzCloud.Create([], 0, cloud.SourcePath);
        }

        var data = new byte[kept * XyzCloud.Stride];
        var write = 0;

        for (var start = 0; start < count; start += ScreenMarkChunk)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int end = Math.Min(start + ScreenMarkChunk, count);
            ReadOnlySpan<byte> bytes = cloud.GetBytes(
                (long)start * XyzCloud.Stride, (end - start) * XyzCloud.Stride);

            for (var i = 0; i < end - start; i++)
            {
                if (keep[start + i] == 0)
                {
                    continue;
                }

                bytes.Slice(i * XyzCloud.Stride, XyzCloud.Stride)
                    .CopyTo(data.AsSpan(write * XyzCloud.Stride, XyzCloud.Stride));
                write++;
            }

            progress?.Report(0.8 + (0.2 * end / count));
        }

        progress?.Report(1);
        return XyzCloud.Create(data, kept, cloud.SourcePath);
    }

    /// <summary>
    /// 点是否落在笔迹（折线，两端圆头）内。<paramref name="invAspect"/> 把 v 折算到与 u 同尺度，
    /// 于是「半径」在像素空间是各向同性的圆。
    /// </summary>
    private static bool IsInsideBrush(
        Vector3 point, in Matrix4x4 viewProjection, ReadOnlySpan<Vector2> path,
        float radiusSq, float invAspect,
        float minU, float maxU, float minV, float maxV)
    {
        Vector4 clip = Vector4.Transform(new Vector4(point, 1f), viewProjection);
        if (clip.W <= 1e-6f)
        {
            return false;
        }

        float inverse = 1f / clip.W;
        float u = ((clip.X * inverse) + 1f) * 0.5f;
        float v = (1f - (clip.Y * inverse)) * 0.5f;

        if (u < minU || u > maxU || v < minV || v > maxV)
        {
            return false;
        }

        float y = v * invAspect;
        float prevX = path[0].X;
        float prevY = path[0].Y * invAspect;

        if (path.Length == 1)
        {
            return DistanceSquared(u, y, prevX, prevY) <= radiusSq;
        }

        for (var i = 1; i < path.Length; i++)
        {
            float curX = path[i].X;
            float curY = path[i].Y * invAspect;
            if (SquaredDistanceToSegment(u, y, prevX, prevY, curX, curY) <= radiusSq)
            {
                return true;
            }

            prevX = curX;
            prevY = curY;
        }

        return false;
    }

    private static float DistanceSquared(float ax, float ay, float bx, float by)
    {
        float dx = ax - bx;
        float dy = ay - by;
        return (dx * dx) + (dy * dy);
    }

    /// <summary>点到线段的最短距离平方（<paramref name="t"/> 夹在 [0,1]，天然包含两端圆头）。</summary>
    private static float SquaredDistanceToSegment(
        float px, float py, float ax, float ay, float bx, float by)
    {
        float vx = bx - ax;
        float vy = by - ay;
        float wx = px - ax;
        float wy = py - ay;
        float lengthSq = (vx * vx) + (vy * vy);
        if (lengthSq <= float.Epsilon)
        {
            return (wx * wx) + (wy * wy);
        }

        float t = Math.Clamp(((wx * vx) + (wy * vy)) / lengthSq, 0f, 1f);
        float dx = wx - (t * vx);
        float dy = wy - (t * vy);
        return (dx * dx) + (dy * dy);
    }

    /// <summary>点的投影是否落在 NDC 矩形内；相机后方的点（w ≤ 0）恒不在。</summary>
    private static bool IsInsideScreen(
        Vector3 point, in Matrix4x4 viewProjection, float minX, float maxX, float minY, float maxY)
    {
        Vector4 clip = Vector4.Transform(new Vector4(point, 1f), viewProjection);
        if (clip.W <= 1e-6f)
        {
            return false;
        }

        float inverse = 1f / clip.W;
        float x = clip.X * inverse;
        float y = clip.Y * inverse;
        return x >= minX && x <= maxX && y >= minY && y <= maxY;
    }

    /// <summary>
    /// 体素降采样：每个体素输出一个点（体素内所有点的质心），NaN/Inf 点丢弃。
    /// </summary>
    /// <param name="voxelSize">体素边长（米），0.1~0.5 常见，越大越稀。</param>
    public static XyzCloud VoxelDownsample(
        XyzCloud cloud, float voxelSize,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cloud);

        if (!(voxelSize > 0f) || !float.IsFinite(voxelSize))
        {
            throw new ArgumentOutOfRangeException(nameof(voxelSize), "体素边长必须为正的有限值。");
        }

        int count = cloud.PointCount;
        if (count == 0)
        {
            return XyzCloud.Create([], 0, cloud.SourcePath);
        }

        ReadOnlySpan<byte> bytes = cloud.GetBytes(0, checked((int)cloud.ByteLength));
        Vector3 origin = cloud.Bounds.Min;
        double inverse = 1.0 / voxelSize;

        // 升序体素表只与「占用体素数」有关，通常几十万，不按包围盒格子数开（862 m @0.5 m 有 51 亿格）
        var accumulator = new VoxelAccumulator(Math.Min(count, 1 << 12));

        for (var i = 0; i < count; i++)
        {
            if ((i & ProgressStride) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report((double)i / count);
            }

            Vector3 p = VoxelHash.ReadPoint(bytes, i);
            if (!VoxelHash.IsFinite(p))
            {
                continue;
            }

            long key = VoxelHash.Key(p, origin, inverse);
            if (key < 0)
            {
                // 超出打包范围（0.01 m 体素下 ±10 km）：跳过，不让一个脏点废掉整次处理
                continue;
            }

            accumulator.Add(key, p);
        }

        int voxels = accumulator.Count;
        var data = new byte[voxels * XyzCloud.Stride];
        accumulator.WriteCentroids(data);

        progress?.Report(1);
        return XyzCloud.Create(data, voxels, cloud.SourcePath);
    }

    /// <summary>
    /// 统计离群点移除（SOR）：每个点到最近 <paramref name="neighborCount"/> 个点的平均距离，
    /// 超过「均值 + stdRatio × 标准差」的点删掉。
    /// </summary>
    /// <param name="neighborCount">近邻个数，常用 8~30。</param>
    /// <param name="stdRatio">阈值倍数，越小删得越多（1.0 大约删 10~15%）。</param>
    /// <remarks>
    /// 32 M 点直接做会以分钟计（每点要扫几十~几百个邻居），界面上应引导「先降采样再 SOR」。
    /// </remarks>
    public static XyzCloud RemoveStatisticalOutliers(
        XyzCloud cloud, int neighborCount = 8, float stdRatio = 1.0f,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cloud);

        if (neighborCount is < 2 or > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(neighborCount), "近邻个数应在 2~128 之间。");
        }

        if (!(stdRatio >= 0f) || !float.IsFinite(stdRatio))
        {
            throw new ArgumentOutOfRangeException(nameof(stdRatio), "标准差倍数必须是非负有限值。");
        }

        int count = cloud.PointCount;
        if (count == 0)
        {
            return XyzCloud.Create([], 0, cloud.SourcePath);
        }

        ReadOnlySpan<byte> bytes = cloud.GetBytes(0, checked((int)cloud.ByteLength));

        // NaN/Inf 点先剔掉，它们算不出可信的邻居距离
        var points = new Vector3[count];
        var validCount = 0;
        for (var i = 0; i < count; i++)
        {
            if ((i & ProgressStride) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report((double)i / count * 0.1);
            }

            Vector3 p = VoxelHash.ReadPoint(bytes, i);
            if (VoxelHash.IsFinite(p))
            {
                points[validCount++] = p;
            }
        }

        if (validCount == 0)
        {
            return XyzCloud.Create([], 0, cloud.SourcePath);
        }

        if (validCount <= neighborCount)
        {
            return PackPoints(points, validCount, cloud.SourcePath);
        }

        float cellSize = EstimateCellSize(cloud.Bounds, validCount, neighborCount);
        var grid = new VoxelGridIndex(cloud.Bounds.Min, cellSize, points, validCount);

        var meanDistances = new float[validCount];

        double total = 0;
        var measured = 0;

        if (validCount < 200_000)
        {
            var nearestDistance = new float[neighborCount];
            var nearestIndex = new int[neighborCount];

            for (var i = 0; i < validCount; i++)
            {
                if ((i & 0xFFFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report(0.1 + ((double)i / validCount * 0.7));
                }

                float average = grid.AverageNearestDistance(i, neighborCount, nearestDistance, nearestIndex, out int found);
                if (found == 0)
                {
                    // 5×5×5 邻域里一个点都没有：这就是孤点，必删；不参与均值/方差，否则一串孤点会把阈值抬到天上
                    meanDistances[i] = float.NaN;
                    continue;
                }

                meanDistances[i] = average;
                total += average;
                measured++;
            }
        }
        else
        {
            // 查询阶段网格只读、各点写自己的下标，天然可并行。
            // 单线程跑 5 M 点要一分多钟：近处的点密度是远处的几十倍，格子内候选数长尾，
            // 每点 10 μs 上下。按核数并行后接近线性。
            var done = 0;
            var gate = new object();

            Parallel.For(
                0,
                validCount,
                () => (Distance: new float[neighborCount], Index: new int[neighborCount], Sum: 0.0, Count: 0),
                (i, _, local) =>
                {
                    float average = grid.AverageNearestDistance(i, neighborCount, local.Distance, local.Index, out int found);
                    if (found == 0)
                    {
                        meanDistances[i] = float.NaN;
                    }
                    else
                    {
                        meanDistances[i] = average;
                        local.Sum += average;
                        local.Count++;
                    }

                    if ((Interlocked.Increment(ref done) & 0xFFFF) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        progress?.Report(0.1 + ((double)done / validCount * 0.7));
                    }

                    return local;
                },
                local =>
                {
                    lock (gate)
                    {
                        total += local.Sum;
                        measured += local.Count;
                    }
                });
        }

        double mean = measured > 0 ? total / measured : 0;
        double sumSquares = 0;
        for (var i = 0; i < validCount; i++)
        {
            if (float.IsNaN(meanDistances[i]))
            {
                continue;
            }

            double delta = meanDistances[i] - mean;
            sumSquares += delta * delta;
        }

        double threshold = mean + (stdRatio * Math.Sqrt(sumSquares / Math.Max(measured, 1)));

        // 均匀点云的标准差是 0，阈值正好等于均值：纯浮点舍入就会误删边界点，加一点相对容差
        double tolerance = Math.Max(threshold * 1e-6, 1e-9);

        var kept = 0;
        for (var i = 0; i < validCount; i++)
        {
            if (meanDistances[i] <= threshold + tolerance)
            {
                points[kept++] = points[i];
            }
        }

        progress?.Report(0.9);
        XyzCloud result = PackPoints(points, kept, cloud.SourcePath);
        progress?.Report(1);
        return result;
    }

    /// <summary>估计近邻网格的体素边长：让每格平均落 4 个点（3×3×3 就有 ~100 个候选，够 k 且不至于爆）。</summary>
    /// <remarks>
    /// <para>
    /// 巡检点云是「表面」数据。按包围盒体积算密度，705×493×37 m 的空心场景会估成 1.3 点/m³、
    /// 体素边长 2.3 m，每格上千个点：16 M 点的 SOR 从「几秒」变成「几分钟还没跑完」。
    /// 改用包围盒表面积当分布面积（21 点/m² → 点间距 0.22 m），量级才对。
    /// </para>
    /// <para>
    /// 格子估小了只是多扩一环（正确性无损），估大了才是灾难，所以这里宁可偏小。
    /// </para>
    /// </remarks>
    private static float EstimateCellSize(Bounds3 bounds, int pointCount, int neighborCount)
    {
        Vector3 size = bounds.Size;
        double maxEdge = Math.Max(size.X, Math.Max(size.Y, size.Z));

        // 两轴退化（单条扫描线）时表面积接近 0，退回用最长边撑一个「一维」面积
        double area = 2 * (((double)size.X * size.Y) + ((double)size.Y * size.Z) + ((double)size.Z * size.X));
        if (!(area > 0))
        {
            area = Math.Pow(Math.Max(maxEdge, 1e-3), 2);
        }

        double density = pointCount / area;
        double spacing = 1.0 / Math.Sqrt(Math.Max(density, 1e-9));

        // 每格目标 1 点：3×3×3 就有 ~27 个候选，几乎总能凑够 k，不必扩到 5×5×5。
        // 格子放大到「每格 4 点」看似更保险，实际表面云在格子内多层折叠，候选数会翻十倍，
        // 随机访存多到 16 M 点要跑几分钟。
        double cell = spacing * Math.Sqrt(Math.Max(neighborCount, 1) / 8.0);

        return (float)Math.Clamp(cell, 1e-3, Math.Max(maxEdge / 4, 1e-3));
    }

    private static XyzCloud PackPoints(Vector3[] points, int count, string sourcePath)
    {
        var data = new byte[count * XyzCloud.Stride];
        for (var i = 0; i < count; i++)
        {
            VoxelHash.WritePoint(data, i, points[i]);
        }

        return XyzCloud.Create(data, count, sourcePath);
    }

    private static bool IsInside(Vector3 point, Bounds3 box)
        => point.X >= box.Min.X && point.X <= box.Max.X
           && point.Y >= box.Min.Y && point.Y <= box.Max.Y
           && point.Z >= box.Min.Z && point.Z <= box.Max.Z;
}
