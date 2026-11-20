using MyPcdEditor.Core.Grid;

namespace MyPcdEditor.Core.Tests;

public sealed class MapViewTests
{
    private static MapViewTransform CreateTransform()
    {
        var transform = new MapViewTransform();
        transform.Resize(800, 600);
        transform.FitToWorld(-100, -50, 300, 150, margin: 0);
        return transform;
    }

    [Fact]
    public void FitToWorld_KeepsExtentInsideViewport()
    {
        MapViewTransform transform = CreateTransform();

        (double minX, double minY, double maxX, double maxY) = transform.VisibleWorld;

        Assert.True(minX <= -100 && maxX >= 300, $"X 未覆盖：{minX}..{maxX}");
        Assert.True(minY <= -50 && maxY >= 150, $"Y 未覆盖：{minY}..{maxY}");
        Assert.Equal(100, transform.CenterX, 6); // (-100+300)/2
        Assert.Equal(50, transform.CenterY, 6);
    }

    [Fact]
    public void ScreenAndWorld_RoundTrip()
    {
        MapViewTransform transform = CreateTransform();
        double[] samples = [0, 17.5, 400, 799.5];

        foreach (double sx in samples)
        {
            foreach (double sy in samples)
            {
                (double wx, double wy) = transform.ScreenToWorld(sx, sy);
                (double backX, double backY) = transform.WorldToScreen(wx, wy);

                Assert.Equal(sx, backX, 6);
                Assert.Equal(sy, backY, 6);
            }
        }
    }

    [Fact]
    public void ScreenYDown_MatchesWorldYUp()
    {
        MapViewTransform transform = CreateTransform();

        (_, double topY) = transform.ScreenToWorld(400, 0);
        (_, double bottomY) = transform.ScreenToWorld(400, 600);

        Assert.True(topY > bottomY, "屏幕上方应对应更大的世界 Y");
    }

    [Fact]
    public void ZoomAt_KeepsAnchorPointFixed()
    {
        MapViewTransform transform = CreateTransform();
        (double anchorX, double anchorY) = transform.ScreenToWorld(600, 120);

        transform.ZoomAt(600, 120, 2.5);

        (double afterX, double afterY) = transform.ScreenToWorld(600, 120);
        Assert.Equal(anchorX, afterX, 6);
        Assert.Equal(anchorY, afterY, 6);
    }

    [Fact]
    public void Pan_MovesViewOppositeToDrag()
    {
        MapViewTransform transform = CreateTransform();
        double before = transform.CenterX;

        transform.Pan(100, 0); // 往右拖，视野中心应该往左走

        Assert.True(transform.CenterX < before);
    }

    [Theory]
    [InlineData(255, OccupancyCell.Free)]
    [InlineData(254, OccupancyCell.Free)]
    [InlineData(205, OccupancyCell.Unknown)]
    [InlineData(100, OccupancyCell.Unknown)]
    [InlineData(0, OccupancyCell.Occupied)]
    public void Classify_UsesRosSemantics(byte value, OccupancyCell expected)
    {
        Assert.Equal(expected, OccupancyPalette.Classify(value, negate: false, 0.65, 0.196));
    }

    [Fact]
    public void Classify_NegateFlipsSemantics()
    {
        // negate=true 时 occ = v/255：白=占用、黑=空闲
        Assert.Equal(OccupancyCell.Occupied, OccupancyPalette.Classify(255, negate: true, 0.65, 0.196));
        Assert.Equal(OccupancyCell.Free, OccupancyPalette.Classify(0, negate: true, 0.65, 0.196));
    }

    /// <summary>8×8 图：左上 2×2 是墙（0），其余空闲（254）。</summary>
    private static PgmImage CreateSmallMap()
    {
        var pixels = new byte[8 * 8];
        Array.Fill(pixels, (byte)254);
        for (var y = 0; y < 2; y++)
        {
            for (var x = 0; x < 2; x++)
            {
                pixels[(y * 8) + x] = 0;
            }
        }

        return new PgmImage { Width = 8, Height = 8, MaxValue = 255, Pixels = pixels };
    }

    [Fact]
    public void GroundTexture_DownsamplesWithMaxPooling()
    {
        var meta = new MapYaml { Resolution = 0.5, OriginX = -10, OriginY = 20 };

        GroundTexture? texture = GroundTexture.Build(CreateSmallMap(), meta, maxSize: 4);

        Assert.NotNull(texture);
        Assert.Equal(4, texture!.Width);
        Assert.Equal(4, texture.Height);
        Assert.Equal(2, texture.BlockSize);
        Assert.Equal(4 * 4 * 4, texture.Rgba.Length);

        // 左上角那格覆盖了 2×2 的墙 → 占用色 + 不透明
        Assert.Equal(OccupancyPalette.GlOccupiedR, texture.Rgba[0]);
        Assert.Equal(OccupancyPalette.GlOccupiedA, texture.Rgba[3]);

        // 膨胀半径 2：墙从 (0,0) 扩到 (0..2, 0..2)，(1,1) 也变墙
        Assert.Equal(OccupancyPalette.GlOccupiedA, texture.Rgba[(((1 * 4) + 1) * 4) + 3]);

        // 右下角离墙 3 texel → 仍是空闲，半透明
        int last = ((3 * 4) + 3) * 4;
        Assert.Equal(OccupancyPalette.GlFreeR, texture.Rgba[last]);
        Assert.Equal(OccupancyPalette.GlFreeA, texture.Rgba[last + 3]);
        Assert.True(texture.Rgba[last + 3] < OccupancyPalette.GlUnknownA);

        // 覆盖范围按原始尺寸算：8 px × 0.5 m
        Assert.Equal(-10, texture.MinX, 6);
        Assert.Equal(20, texture.MinY, 6);
        Assert.Equal(-6, texture.MaxX, 6);
        Assert.Equal(24, texture.MaxY, 6);
    }

    [Fact]
    public void GroundTexture_UnknownKeepsDistinctAlpha()
    {
        var pixels = new byte[4 * 4];
        Array.Fill(pixels, (byte)205); // 未知
        var image = new PgmImage { Width = 4, Height = 4, MaxValue = 255, Pixels = pixels };

        GroundTexture? texture = GroundTexture.Build(image, new MapYaml(), maxSize: 64);

        Assert.NotNull(texture);
        Assert.Equal(1, texture!.BlockSize); // 不需要降采样
        Assert.Equal(OccupancyPalette.GlUnknownA, texture.Rgba[3]);
    }

    /// <summary>地面纹理的三类 alpha 必须严格递降，否则墙/未知/空闲在地面上分不出层次。</summary>
    [Fact]
    public void GroundTexture_AlphaOrderIsWallThenUnknownThenFree()
    {
        Assert.True(OccupancyPalette.GlOccupiedA > OccupancyPalette.GlUnknownA);
        Assert.True(OccupancyPalette.GlUnknownA > OccupancyPalette.GlFreeA);
        Assert.True(OccupancyPalette.GlFreeA > 0);
    }
}
