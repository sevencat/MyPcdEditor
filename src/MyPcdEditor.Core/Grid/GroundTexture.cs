namespace MyPcdEditor.Core.Grid;

/// <summary>
/// 3D 场景里当地面贴的栅格纹理：PGM 按整数块 max-pooling 降到 GPU 友好尺寸后的 RGBA 数据。
/// </summary>
/// <remarks>
/// <para>
/// 原始 PGM 是 6811×4503（30.7 M px），直接传 RGBA 要 122 MB 显存，且缩小显示时靠 mipmap
/// 会把 1~2 px 的墙糊掉。这里先在 CPU 侧按块取"占用 &gt; 未知 &gt; 空闲"的最大类别
/// （2048 上限 → block=4 → 1703×1126 ≈ 7.7 MB），墙体不会因为降采样而消失。
/// </para>
/// <para>行序与 PGM 一致：第 0 行是世界 y 最大的那一行，上传后 v=0 对应 MaxY。</para>
/// </remarks>
public sealed class GroundTexture
{
    /// <summary>纹理单边上限；GLES 3.0 最低保证 2048。</summary>
    public const int DefaultMaxSize = 2048;

    /// <summary>墙体显示加粗半径（texel），见 <see cref="DilateOccupied"/>。</summary>
    private const int DilationRadius = 2;

    private GroundTexture(int width, int height, byte[] rgba, double minX, double minY, double maxX, double maxY)
    {
        Width = width;
        Height = height;
        Rgba = rgba;
        MinX = minX;
        MinY = minY;
        MaxX = maxX;
        MaxY = maxY;
    }

    /// <summary>纹理宽（像素）。</summary>
    public int Width { get; }

    /// <summary>纹理高（像素）。</summary>
    public int Height { get; }

    /// <summary>RGBA8888，行主序，长度 = Width × Height × 4。</summary>
    public byte[] Rgba { get; }

    /// <summary>纹理覆盖的世界范围（米）。</summary>
    public double MinX { get; }

    public double MinY { get; }

    public double MaxX { get; }

    public double MaxY { get; }

    /// <summary>降采样用的块边长（原始像素 / 纹理像素）。</summary>
    public int BlockSize { get; private init; } = 1;

    /// <summary>占用（墙体）texel 个数，诊断用。</summary>
    public int CountOccupied()
    {
        int count = 0;
        for (int i = 3; i < Rgba.Length; i += 4)
        {
            if (Rgba[i] == OccupancyPalette.GlOccupiedA)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>生成纹理数据；图为空或参数非法时返回 <c>null</c>。</summary>
    public static GroundTexture? Build(PgmImage image, MapYaml meta, int maxSize = DefaultMaxSize)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(meta);

        if (image.Width <= 0 || image.Height <= 0 || image.Pixels.Length < image.Width * image.Height)
        {
            return null;
        }

        maxSize = Math.Max(maxSize, 1);
        int block = Math.Max(1, (int)Math.Ceiling(Math.Max(image.Width, image.Height) / (double)maxSize));
        int outWidth = (image.Width + block - 1) / block;
        int outHeight = (image.Height + block - 1) / block;

        byte[] classes = BuildClassLut(meta);
        var classMap = new byte[outWidth * outHeight];

        for (var oy = 0; oy < outHeight; oy++)
        {
            int y0 = oy * block;
            int y1 = Math.Min(y0 + block, image.Height);

            for (var ox = 0; ox < outWidth; ox++)
            {
                int x0 = ox * block;
                int x1 = Math.Min(x0 + block, image.Width);

                byte worst = 0;
                for (int y = y0; y < y1; y++)
                {
                    int rowBase = y * image.Width;
                    for (int x = x0; x < x1; x++)
                    {
                        byte cls = classes[image.Pixels[rowBase + x]];
                        if (cls > worst)
                        {
                            worst = cls;
                        }
                    }
                }

                classMap[(oy * outWidth) + ox] = worst;
            }
        }

        DilateOccupied(classMap, outWidth, outHeight, DilationRadius);

        var rgba = new byte[outWidth * outHeight * 4];
        for (var i = 0; i < classMap.Length; i++)
        {
            (byte r, byte g, byte b, byte a) = OccupancyPalette.ToGlRgba((OccupancyCell)classMap[i]);
            rgba[i * 4] = r;
            rgba[(i * 4) + 1] = g;
            rgba[(i * 4) + 2] = b;
            rgba[(i * 4) + 3] = a;
        }

        (double minX, double minY, double maxX, double maxY) = meta.WorldExtent(image.Width, image.Height);

        return new GroundTexture(outWidth, outHeight, rgba, minX, minY, maxX, maxY) { BlockSize = block };
    }

    /// <summary>
    /// 把占用像素向外膨胀<paramref name="radius"/>圈。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 降采样后墙通常只剩 1~2 个 texel 宽。1703 px 宽的纹理铺 680 m 的地面，全景视角下屏幕上
    /// 每像素只对应 1.7 texel，1 texel 的亮线画出来就是一根 1 px 的细白线，眼睛基本分辨不出
    /// 墙的形状（实测整片地面看上去只是一块淡淡的灰板）。
    /// </para>
    /// <para>
    /// 半径 2（5×5）后线条约 5 texel ≈ 3 屏幕像素，墙与点云墙体的对齐关系一眼就能看出来。
    /// 只影响显示用纹理，不动原始栅格数据。
    /// </para>
    /// </remarks>
    private static void DilateOccupied(byte[] classMap, int width, int height, int radius)
    {
        if (radius <= 0)
        {
            return;
        }

        var snapshot = (byte[])classMap.Clone();

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (snapshot[(y * width) + x] != (byte)OccupancyCell.Occupied)
                {
                    continue;
                }

                int y0 = Math.Max(y - radius, 0);
                int y1 = Math.Min(y + radius, height - 1);
                int x0 = Math.Max(x - radius, 0);
                int x1 = Math.Min(x + radius, width - 1);

                for (int ny = y0; ny <= y1; ny++)
                {
                    for (int nx = x0; nx <= x1; nx++)
                    {
                        classMap[(ny * width) + nx] = (byte)OccupancyCell.Occupied;
                    }
                }
            }
        }
    }

    /// <summary>灰度值 → 类别下标（0 空闲 / 1 未知 / 2 占用），避免逐像素走 double 运算。</summary>
    private static byte[] BuildClassLut(MapYaml meta)
    {
        var lut = new byte[256];
        for (var value = 0; value < 256; value++)
        {
            lut[value] = (byte)OccupancyPalette.Classify((byte)value, meta);
        }

        return lut;
    }
}
