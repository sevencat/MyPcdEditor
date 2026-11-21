using System.Numerics;
using MyPcdEditor.Core.Grid;

namespace MyPcdEditor.Core.Tests;

/// <summary>2D 栅格笔刷擦除：折线胶囊 + 灰度值选择。</summary>
public sealed class GridEraserTests
{
    private const byte Occupied = 0;
    private const byte Free = 254;
    private const byte Unknown = GridEraser.UnknownValue;

    private static readonly MapYaml Meta = new();

    private static PgmImage MakeImage(int width, int height, byte fill = Occupied)
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

    private static byte Get(PgmImage image, int x, int y) => image.Pixels[(y * image.Width) + x];

    /// <summary>单点笔迹 = 一个圆点：半径 1.5 覆盖 3×3。</summary>
    [Fact]
    public void Erase_PaintsUnknownAroundDot()
    {
        PgmImage image = MakeImage(10, 10);

        GridMorphResult result = GridEraser.Erase(image, Meta, [new Vector2(5, 5)], 1.5);

        Assert.Equal(Unknown, Get(result.Image, 5, 5));
        Assert.Equal(Unknown, Get(result.Image, 4, 4));
        Assert.Equal(Occupied, Get(result.Image, 7, 5));
        Assert.Equal(9, result.ChangedPixels);
        // 输入图不动
        Assert.Equal(Occupied, Get(image, 5, 5));
    }

    /// <summary>折线段按「点到线段距离 ≤ 半径」铺开，两端是圆头。</summary>
    [Fact]
    public void Erase_PaintsAlongSegment()
    {
        PgmImage image = MakeImage(12, 12);

        GridMorphResult result = GridEraser.Erase(
            image, Meta, [new Vector2(3, 6), new Vector2(8, 6)], 1);

        // 轴线上列 2..9（两端各鼓出半径），上下各一行只有列 3..8（斜角距离 √2 已出圈）
        Assert.Equal(Unknown, Get(result.Image, 2, 6));
        Assert.Equal(Unknown, Get(result.Image, 9, 6));
        Assert.Equal(Unknown, Get(result.Image, 5, 5));
        Assert.Equal(Unknown, Get(result.Image, 5, 7));
        Assert.Equal(Occupied, Get(result.Image, 1, 6));
        Assert.Equal(Occupied, Get(result.Image, 10, 6));
        Assert.Equal(Occupied, Get(result.Image, 5, 8));
        Assert.Equal(8 + 6 + 6, result.ChangedPixels);
    }

    /// <summary>刷成空闲时写 254，而不是未知值。</summary>
    [Fact]
    public void Erase_FreeTargetUsesFreeValue()
    {
        PgmImage image = MakeImage(8, 8, fill: Occupied);

        GridMorphResult result = GridEraser.Erase(
            image, Meta, [new Vector2(4, 4)], 1, OccupancyCell.Free);

        Assert.Equal(Free, Get(result.Image, 4, 4));
        Assert.Equal(OccupancyCell.Free, OccupancyPalette.Classify(Get(result.Image, 4, 4), Meta));
    }

    /// <summary>negate 的图灰度含义相反：未知要写 50，空闲写 1，否则会被读成占用。</summary>
    [Fact]
    public void ValueFor_RespectsNegate()
    {
        var negated = new MapYaml { Negate = true };

        Assert.Equal((byte)50, GridEraser.ValueFor(OccupancyCell.Unknown, negated));
        Assert.Equal((byte)1, GridEraser.ValueFor(OccupancyCell.Free, negated));
        Assert.Equal(OccupancyCell.Unknown, OccupancyPalette.Classify(50, negated));
        Assert.Equal(OccupancyCell.Free, OccupancyPalette.Classify(1, negated));
    }

    [Fact]
    public void ValueFor_MatchesClassifyForDefaultMeta()
    {
        Assert.Equal(Unknown, GridEraser.ValueFor(OccupancyCell.Unknown, Meta));
        Assert.Equal(Free, GridEraser.ValueFor(OccupancyCell.Free, Meta));
        Assert.Equal(Occupied, GridEraser.ValueFor(OccupancyCell.Occupied, Meta));
    }

    [Fact]
    public void Erase_IgnoresPixelsAlreadyTargeted()
    {
        PgmImage image = MakeImage(6, 6, fill: Unknown);

        GridMorphResult result = GridEraser.Erase(image, Meta, [new Vector2(3, 3)], 2);

        Assert.Equal(0, result.ChangedPixels);
    }

    [Fact]
    public void Erase_ClipsAtImageBorder()
    {
        PgmImage image = MakeImage(6, 6);

        // 圆心在左上角外，半径盖住图内的一个角
        GridMorphResult result = GridEraser.Erase(image, Meta, [new Vector2(-2, -2)], 3);

        Assert.True(result.ChangedPixels > 0);
        Assert.Equal(Unknown, Get(result.Image, 0, 0));
        Assert.Equal(Occupied, Get(result.Image, 5, 5));
    }

    [Fact]
    public void Erase_RejectsBadArguments()
    {
        PgmImage image = MakeImage(6, 6);

        Assert.Throws<ArgumentException>(() => GridEraser.Erase(image, Meta, [], 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => GridEraser.Erase(image, Meta, [new Vector2(3, 3)], 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => GridEraser.Erase(
            image, Meta, [new Vector2(3, 3)], 1, OccupancyCell.Occupied));
    }
}
