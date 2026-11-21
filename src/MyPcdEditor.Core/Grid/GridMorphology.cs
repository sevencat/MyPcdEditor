namespace MyPcdEditor.Core.Grid;

/// <summary>栅格形态学处理结果：新图 + 改动统计（供状态栏/日志）。</summary>
/// <param name="Image">处理后的新图（输入图不变）。</param>
/// <param name="ChangedPixels">与输入图灰度不同的像素数。</param>
/// <param name="RemovedIslands">去掉/填上的连通块数（只有去孤岛/补洞有意义）。</param>
public readonly record struct GridMorphResult(PgmImage Image, int ChangedPixels, int RemovedIslands);

/// <summary>
/// occupancy 栅格的形态学处理：去毛刺（开运算）、补洞（闭运算）、去孤岛（小连通块）。
/// </summary>
/// <remarks>
/// <para>
/// 一切都在「占用掩码」上做（<c>OccupancyPalette.Classify</c>，与 2D 视图、图例完全同一判据），
/// 改完再写回灰度：被抹掉的占用像素填**未知(205)** 而不是空闲——地图工具不应替用户断言"这里能走"。
/// </para>
/// <para>
/// 核用半径参数：<c>radius=1</c> 是 3×3 全核（能吃掉对角相邻的毛刺），
/// 更大时退化成圆形核（<c>dx²+dy² ≤ r²+1</c>），避免方块核把斜边啃出直角。
/// </para>
/// </remarks>
public static class GridMorphology
{
    /// <summary>map_server 惯例的未知灰度值。</summary>
    public const byte UnknownValue = 205;

    /// <summary>去毛刺：先腐蚀后膨胀（开运算），抹掉孤立的小块占用。</summary>
    public static GridMorphResult Open(PgmImage source, MapYaml meta, int radius = 1)
    {
        ValidateRadius(radius);

        bool[] mask = ToMask(source, meta);
        bool[] opened = Dilate(Erode(mask, source.Width, source.Height, radius), source.Width, source.Height, radius);

        return Apply(source, mask, opened, meta, removedIslands: 0);
    }

    /// <summary>
    /// 轻度去毛刺：只删「8 邻域占用数 ≤ <paramref name="maxNeighbors"/>」的占用像素，迭代若干轮。
    /// </summary>
    /// <remarks>
    /// 这是实际用来替代 <see cref="Open"/> 的默认去毛刺方式：开运算要求核内**全**占用，
    /// 1 像素宽的细墙（0.05 m 分辨率下很常见）会被整条抹掉；而按邻居数删只吃孤点/尖端，
    /// 直线中段（邻居 2）保留，端点（邻居 1）每轮退一格。
    /// </remarks>
    public static GridMorphResult Despeckle(PgmImage source, MapYaml meta, int maxNeighbors = 1, int iterations = 1)
    {
        ArgumentNullException.ThrowIfNull(meta);

        if (maxNeighbors is < 0 or > 7)
        {
            throw new ArgumentOutOfRangeException(nameof(maxNeighbors), "邻居数阈值应在 0~7 之间。");
        }

        if (iterations is < 1 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(iterations), "迭代轮数应在 1~32 之间。");
        }

        int width = source.Width;
        int height = source.Height;
        bool[] original = ToMask(source, meta);
        bool[] mask = (bool[])original.Clone();

        for (var iteration = 0; iteration < iterations; iteration++)
        {
            bool[] next = (bool[])mask.Clone();
            var changedInRound = false;

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    int index = (y * width) + x;
                    if (!mask[index] || CountNeighbors(mask, width, height, x, y) > maxNeighbors)
                    {
                        continue;
                    }

                    next[index] = false;
                    changedInRound = true;
                }
            }

            if (!changedInRound)
            {
                break;
            }

            mask = next;
        }

        return Apply(source, original, mask, meta, removedIslands: 0);
    }

    /// <summary>补洞：先膨胀后腐蚀（闭运算），把墙上的小缺口/细缝填上。</summary>
    public static GridMorphResult Close(PgmImage source, MapYaml meta, int radius = 1)
    {
        ValidateRadius(radius);

        bool[] mask = ToMask(source, meta);
        bool[] closed = Erode(Dilate(mask, source.Width, source.Height, radius), source.Width, source.Height, radius);

        return Apply(source, mask, closed, meta, removedIslands: 0);
    }

    /// <summary>去孤岛：占用连通块面积小于 <paramref name="minAreaPixels"/> 的整块抹成未知（8 连通）。</summary>
    public static GridMorphResult RemoveIslands(PgmImage source, MapYaml meta, int minAreaPixels)
    {
        ArgumentNullException.ThrowIfNull(meta);

        if (minAreaPixels < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minAreaPixels), "最小面积不能为负。");
        }

        bool[] mask = ToMask(source, meta);
        bool[] filtered = (bool[])mask.Clone();
        int width = source.Width;
        int height = source.Height;
        int total = width * height;

        var visited = new bool[total];
        var queue = new int[total];
        var removed = 0;

        for (var start = 0; start < total; start++)
        {
            if (!mask[start] || visited[start])
            {
                continue;
            }

            int head = 0;
            int tail = 0;
            queue[tail++] = start;
            visited[start] = true;

            while (head < tail)
            {
                int p = queue[head++];
                int px = p % width;
                int py = p / width;

                for (var dy = -1; dy <= 1; dy++)
                {
                    int ny = py + dy;
                    if (ny < 0 || ny >= height)
                    {
                        continue;
                    }

                    for (var dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0)
                        {
                            continue;
                        }

                        int nx = px + dx;
                        if (nx < 0 || nx >= width)
                        {
                            continue;
                        }

                        int n = (ny * width) + nx;
                        if (mask[n] && !visited[n])
                        {
                            visited[n] = true;
                            queue[tail++] = n;
                        }
                    }
                }
            }

            if (tail < minAreaPixels)
            {
                for (var i = 0; i < tail; i++)
                {
                    filtered[queue[i]] = false;
                }

                removed++;
            }
        }

        return Apply(source, mask, filtered, meta, removed);
    }

    private static void ValidateRadius(int radius)
    {
        if (radius is < 1 or > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(radius), "核半径应在 1~16 之间。");
        }
    }

    private static bool[] ToMask(PgmImage source, MapYaml meta)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(meta);

        var mask = new bool[source.Width * source.Height];
        for (var i = 0; i < mask.Length; i++)
        {
            mask[i] = OccupancyPalette.Classify(source.Pixels[i], meta) == OccupancyCell.Occupied;
        }

        return mask;
    }

    /// <summary>8 邻域里占用像素的个数（图外不计）。</summary>
    private static int CountNeighbors(bool[] mask, int width, int height, int x, int y)
    {
        var count = 0;

        for (var dy = -1; dy <= 1; dy++)
        {
            int ny = y + dy;
            if (ny < 0 || ny >= height)
            {
                continue;
            }

            for (var dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0)
                {
                    continue;
                }

                int nx = x + dx;
                if (nx < 0 || nx >= width)
                {
                    continue;
                }

                if (mask[(ny * width) + nx])
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static bool[] Erode(bool[] mask, int width, int height, int radius)
    {
        var result = new bool[mask.Length];
        int squared = (radius * radius) + 1;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                int index = (y * width) + x;
                if (!mask[index])
                {
                    continue;
                }

                var keep = true;
                for (var dy = -radius; dy <= radius && keep; dy++)
                {
                    int ny = y + dy;
                    if (ny < 0 || ny >= height)
                    {
                        continue; // 图外不算"非占用"，否则整幅地图边缘会被削一圈
                    }

                    for (var dx = -radius; dx <= radius; dx++)
                    {
                        if ((dx * dx) + (dy * dy) > squared)
                        {
                            continue;
                        }

                        int nx = x + dx;
                        if (nx < 0 || nx >= width)
                        {
                            continue;
                        }

                        if (!mask[(ny * width) + nx])
                        {
                            keep = false;
                            break;
                        }
                    }
                }

                result[index] = keep;
            }
        }

        return result;
    }

    private static bool[] Dilate(bool[] mask, int width, int height, int radius)
    {
        var result = new bool[mask.Length];
        int squared = (radius * radius) + 1;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                int index = (y * width) + x;
                if (mask[index])
                {
                    result[index] = true;
                    continue;
                }

                var hit = false;
                for (var dy = -radius; dy <= radius && !hit; dy++)
                {
                    int ny = y + dy;
                    if (ny < 0 || ny >= height)
                    {
                        continue;
                    }

                    for (var dx = -radius; dx <= radius; dx++)
                    {
                        if ((dx * dx) + (dy * dy) > squared)
                        {
                            continue;
                        }

                        int nx = x + dx;
                        if (nx < 0 || nx >= width)
                        {
                            continue;
                        }

                        if (mask[(ny * width) + nx])
                        {
                            hit = true;
                            break;
                        }
                    }
                }

                result[index] = hit;
            }
        }

        return result;
    }

    /// <summary>把新掩码写回灰度：抹掉的填未知、新增的填占用值，其余像素原样保留。</summary>
    private static GridMorphResult Apply(PgmImage source, bool[] before, bool[] after, MapYaml meta, int removedIslands)
    {
        var pixels = (byte[])source.Pixels.Clone();
        byte occupiedValue = meta.Negate ? (byte)255 : (byte)0;
        var changed = 0;

        for (var i = 0; i < pixels.Length; i++)
        {
            if (before[i] == after[i])
            {
                continue;
            }

            if (after[i])
            {
                pixels[i] = occupiedValue; // 闭运算补出来的墙
            }
            else
            {
                pixels[i] = UnknownValue;  // 抹掉的毛刺/孤岛：填未知，不替用户断言"能走"
            }

            changed++;
        }

        var image = new PgmImage
        {
            Width = source.Width,
            Height = source.Height,
            MaxValue = source.MaxValue,
            Pixels = pixels,
        };

        return new GridMorphResult(image, changed, removedIslands);
    }
}
