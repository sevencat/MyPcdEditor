using MyPcdEditor.Core.Grid;

namespace MyPcdEditor.Core.Tests;

/// <summary>occupancy 栅格形态学（去毛刺 / 补洞 / 去孤岛）。</summary>
public sealed class GridMorphologyTests
{
    private const byte Occupied = 0;
    private const byte Free = 255;
    private const byte Unknown = GridMorphology.UnknownValue;

    private static readonly MapYaml Meta = new();

    private static PgmImage MakeImage(int width, int height, byte fill = Free)
    {
        var pixels = new byte[width * height];
        Array.Fill(pixels, fill);

        return new PgmImage
        {
            Width = width,
            Height = height,
            MaxValue = 255,
            Pixels = pixels,
        };
    }

    private static void Set(PgmImage image, int x, int y, byte value)
        => image.Pixels[(y * image.Width) + x] = value;

    private static byte Get(PgmImage image, int x, int y)
        => image.Pixels[(y * image.Width) + x];

    [Fact]
    public void Open_RemovesSinglePixelSpur()
    {
        PgmImage image = MakeImage(7, 7);
        Set(image, 3, 3, Occupied);

        GridMorphResult result = GridMorphology.Open(image, Meta);

        Assert.Equal(Unknown, Get(result.Image, 3, 3));
        Assert.Equal(Free, Get(result.Image, 0, 0));
        Assert.Equal(1, result.ChangedPixels);
        // 输入图不动
        Assert.Equal(Occupied, Get(image, 3, 3));
    }

    [Fact]
    public void Open_KeepsSolidBlock()
    {
        PgmImage image = MakeImage(9, 9);
        for (var y = 3; y <= 5; y++)
        {
            for (var x = 3; x <= 5; x++)
            {
                Set(image, x, y, Occupied);
            }
        }

        GridMorphResult result = GridMorphology.Open(image, Meta);

        for (var y = 3; y <= 5; y++)
        {
            for (var x = 3; x <= 5; x++)
            {
                Assert.Equal(Occupied, Get(result.Image, x, y));
            }
        }

        Assert.Equal(0, result.ChangedPixels);
    }

    [Fact]
    public void Open_ErasesOnePixelWideLine_KnownLimitation()
    {
        // 开运算要求核内全占用，1 像素宽的墙会被整条抹掉——这是它不能当默认去毛刺用的原因
        PgmImage image = MakeImage(9, 9);
        for (var x = 1; x <= 7; x++)
        {
            Set(image, x, 4, Occupied);
        }

        GridMorphResult result = GridMorphology.Open(image, Meta);

        for (var x = 1; x <= 7; x++)
        {
            Assert.Equal(Unknown, Get(result.Image, x, 4));
        }
    }

    [Fact]
    public void Despeckle_RemovesSpeckleButKeepsThinWall()
    {
        PgmImage image = MakeImage(11, 11);
        Set(image, 2, 2, Occupied);                     // 孤点
        for (var x = 3; x <= 8; x++)
        {
            Set(image, x, 6, Occupied);                 // 1 像素宽的墙
        }

        GridMorphResult result = GridMorphology.Despeckle(image, Meta, maxNeighbors: 1);

        Assert.Equal(Unknown, Get(result.Image, 2, 2));
        Assert.Equal(Occupied, Get(result.Image, 5, 6));
        // 端点只有 1 个邻居，按规则退一格；中段（2 个邻居）保留
        Assert.Equal(Unknown, Get(result.Image, 3, 6));
        Assert.Equal(Occupied, Get(result.Image, 4, 6));
    }

    [Fact]
    public void Despeckle_KeepsDiagonalWall()
    {
        // 对角 1 像素墙：每个像素有 2 个对角邻居，不该被削
        PgmImage image = MakeImage(10, 10);
        for (var i = 1; i <= 7; i++)
        {
            Set(image, i, i, Occupied);
        }

        GridMorphResult result = GridMorphology.Despeckle(image, Meta, maxNeighbors: 1);

        for (var i = 2; i <= 6; i++)
        {
            Assert.Equal(Occupied, Get(result.Image, i, i));
        }
    }

    [Fact]
    public void Close_FillsSinglePixelHole()
    {
        PgmImage image = MakeImage(9, 9);
        for (var y = 3; y <= 5; y++)
        {
            for (var x = 3; x <= 5; x++)
            {
                Set(image, x, y, x == 4 && y == 4 ? Free : Occupied);
            }
        }

        GridMorphResult result = GridMorphology.Close(image, Meta);

        Assert.Equal(Occupied, Get(result.Image, 4, 4));
        Assert.Equal(1, result.ChangedPixels);
    }

    [Fact]
    public void Close_DoesNotGrowIntoSolidFreeArea()
    {
        PgmImage image = MakeImage(11, 11);
        Set(image, 5, 5, Occupied);

        GridMorphResult result = GridMorphology.Close(image, Meta);

        // 闭运算 = 膨胀后腐蚀，单个像素占用的结果仍是它自己
        Assert.Equal(Occupied, Get(result.Image, 5, 5));
        Assert.Equal(Free, Get(result.Image, 4, 4));
        Assert.Equal(0, result.ChangedPixels);
    }

    [Fact]
    public void RemoveIslands_DropsSmallClustersOnly()
    {
        PgmImage image = MakeImage(20, 20);

        // 大块 4×4 = 16 像素
        for (var y = 2; y < 6; y++)
        {
            for (var x = 2; x < 6; x++)
            {
                Set(image, x, y, Occupied);
            }
        }

        // 小块 2×2 = 4 像素
        for (var y = 14; y < 16; y++)
        {
            for (var x = 14; x < 16; x++)
            {
                Set(image, x, y, Occupied);
            }
        }

        GridMorphResult result = GridMorphology.RemoveIslands(image, Meta, minAreaPixels: 5);

        Assert.Equal(Occupied, Get(result.Image, 2, 2));
        Assert.Equal(Occupied, Get(result.Image, 5, 5));
        Assert.Equal(Unknown, Get(result.Image, 14, 14));
        Assert.Equal(Unknown, Get(result.Image, 15, 15));
        Assert.Equal(1, result.RemovedIslands);
        Assert.Equal(4, result.ChangedPixels);
    }

    [Fact]
    public void RemoveIslands_TreatsDiagonalsAsConnected()
    {
        PgmImage image = MakeImage(10, 10);
        Set(image, 4, 4, Occupied);
        Set(image, 5, 5, Occupied);

        GridMorphResult result = GridMorphology.RemoveIslands(image, Meta, minAreaPixels: 5);

        // 8 连通下这两个点是同一块（面积 2 < 5），一起被删
        Assert.Equal(Unknown, Get(result.Image, 4, 4));
        Assert.Equal(Unknown, Get(result.Image, 5, 5));
        Assert.Equal(1, result.RemovedIslands);
    }

    [Fact]
    public void RemoveIslands_LeavesFreeAndUnknownUntouched()
    {
        PgmImage image = MakeImage(8, 8);
        Set(image, 2, 2, Unknown);
        Set(image, 6, 6, Unknown);

        GridMorphResult result = GridMorphology.RemoveIslands(image, Meta, minAreaPixels: 10);

        Assert.Equal(0, result.ChangedPixels);
        Assert.Equal(Unknown, Get(result.Image, 2, 2));
    }

    [Fact]
    public void PgmWriter_RoundTripsThroughReader()
    {
        PgmImage image = MakeImage(5, 3);
        Set(image, 1, 1, Occupied);
        Set(image, 2, 1, Unknown);

        string path = Path.Combine(Path.GetTempPath(), $"morph-{Guid.NewGuid():N}.pgm");
        try
        {
            PgmWriter.Write(path, image);
            PgmImage read = PgmReader.Read(path);

            Assert.Equal(image.Width, read.Width);
            Assert.Equal(image.Height, read.Height);
            Assert.Equal(image.MaxValue, read.MaxValue);
            Assert.Equal(image.Pixels, read.Pixels);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
