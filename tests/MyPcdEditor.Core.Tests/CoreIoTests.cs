using System.Text;
using MyPcdEditor.Core;
using MyPcdEditor.Core.Grid;
using MyPcdEditor.Core.PointCloud;

namespace MyPcdEditor.Core.Tests;

public sealed class CoreIoTests
{
    /// <summary>仓库根目录（测试输出在 tests/.../bin/Debug/net11.0 下）。</summary>
    private static string RepoRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MyPcdEditor.slnx")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName ?? AppContext.BaseDirectory;
        }
    }

    private static string SampleMap(string name) => Path.Combine(RepoRoot, "testdata", name);

    // ---- 合成样本：覆盖通用解析路径 ----

    [Fact]
    public void Lzf_Decompress_LiteralOnly()
    {
        // ctrl=2 表示「后面 3 个字节是字面量」
        byte[] input = [2, (byte)'a', (byte)'b', (byte)'c'];
        var output = new byte[16];

        int written = Lzf.Decompress(input, output);

        Assert.Equal(3, written);
        Assert.Equal("abc", Encoding.ASCII.GetString(output, 0, written));
    }

    [Fact]
    public void AsciiCloud_ReadsCoordinatesAndBounds()
    {
        string path = WriteTemp("ascii.pcd", """
            # .PCD v0.7
            VERSION 0.7
            FIELDS x y z
            SIZE 4 4 4
            TYPE F F F
            COUNT 1 1 1
            WIDTH 3
            HEIGHT 1
            VIEWPOINT 0 0 0 1 0 0 0
            POINTS 3
            DATA ascii
            0 0 0
            1 2 3
            -1 -2 -3
            """);

        using XyzCloud cloud = XyzCloud.Open(path);

        Assert.Equal(3, cloud.PointCount);
        Assert.False(cloud.IsZeroCopy);
        Assert.Equal(-1f, cloud.Bounds.Min.X);
        Assert.Equal(3f, cloud.Bounds.Max.Z);
    }

    [Fact]
    public void BinaryCloud_WithExtraFields_UsesAosOffsets()
    {
        // 额外塞一个 intensity 字段，验证按列偏移取值而不是"假设前 12 字节是 xyz"
        string path = Path.Combine(Path.GetTempPath(), $"myPcdEditor_{Guid.NewGuid():N}.pcd");
        try
        {
            const string header = """
                VERSION 0.7
                FIELDS x y z intensity
                SIZE 4 4 4 4
                TYPE F F F F
                COUNT 1 1 1 1
                WIDTH 2
                HEIGHT 1
                POINTS 2
                DATA binary

                """;

            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                byte[] head = new UTF8Encoding(false).GetBytes(header.ReplaceLineEndings("\n"));
                stream.Write(head);
                foreach (float value in new[] { 1f, 2f, 3f, 99f, -4f, -5f, -6f, 42f })
                {
                    stream.Write(BitConverter.GetBytes(value));
                }
            }

            using XyzCloud cloud = XyzCloud.Open(path);

            Assert.Equal(2, cloud.PointCount);
            Assert.False(cloud.IsZeroCopy);
            Assert.Equal(-4f, cloud.Bounds.Min.X);
            Assert.Equal(-6f, cloud.Bounds.Min.Z);
            Assert.Equal(3f, cloud.Bounds.Max.Z);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Pgm_P5_RoundTripsHeaderAndPixels()
    {
        string path = Path.Combine(Path.GetTempPath(), $"myPcdEditor_{Guid.NewGuid():N}.pgm");
        try
        {
            File.WriteAllBytes(path, [.. Encoding.ASCII.GetBytes("P5\n2 2\n255\n"), 0, 50, 200, 255]);

            PgmImage image = PgmReader.Read(path);

            Assert.Equal(2, image.Width);
            Assert.Equal(2, image.Height);
            Assert.Equal(255, image.MaxValue);
            Assert.Equal(0, image[0, 0]);
            Assert.Equal(255, image[1, 1]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MapYaml_ParsesOriginAndThresholds()
    {
        MapYaml yaml = MapYaml.Parse("""
            image: /home/ysc/jy_cog/system/maps/map-20261116-180314/jueying.pgm
            resolution: 0.100000
            origin: [-275.9, -259.0, 0.00]
            negate: 0
            occupied_thresh: 0.65
            free_thresh: 0.196
            """);

        Assert.Equal(0.1, yaml.Resolution, 6);
        Assert.Equal(-275.9, yaml.OriginX, 6);
        Assert.Equal(-259.0, yaml.OriginY, 6);
        Assert.False(yaml.Negate);
        Assert.Equal(0.65, yaml.OccupiedThresh, 6);
    }

    // ---- 真实样例：缺失时自动跳过，不阻塞构建 ----

    [Theory]
    [InlineData("map-20261116-180314", 16193746)]
    [InlineData("map-20261116-210819", 32460950)]
    public void RealPackage_OpensZeroCopyWithExpectedPointCount(string folder, int expectedPoints)
    {
        string directory = SampleMap(folder);
        if (!Directory.Exists(directory))
        {
            return; // 未放置样例数据（.gitignore 排除）
        }

        MapPackage package = MapPackage.Open(directory);

        Assert.NotNull(package.PcdPath);
        Assert.NotNull(package.PgmPath);
        Assert.NotNull(package.Yaml);
        Assert.NotNull(package.ProjectedOrigin);
        Assert.Equal(21, package.ProjectedOrigin!.Zone);

        using XyzCloud cloud = XyzCloud.Open(package.PcdPath!);

        Assert.Equal(expectedPoints, cloud.PointCount);
        Assert.True(cloud.IsZeroCopy, "样例应为 float32 xyz + binary，走零拷贝路径");
        Assert.True(cloud.Bounds.Size.X > 100, "包围盒 X 跨度应大于 100 米");
        Assert.True(cloud.Bounds.Size.Z > 10, "包围盒 Z 跨度应大于 10 米");
    }

    [Fact]
    public void RealPackage_GridDimensionsMatchFileSize()
    {
        string directory = SampleMap("map-20261116-180314");
        if (!Directory.Exists(directory))
        {
            return;
        }

        MapPackage package = MapPackage.Open(directory);
        PgmImage image = PgmReader.Read(package.PgmPath!);

        Assert.Equal(6811, image.Width);
        Assert.Equal(4503, image.Height);
        Assert.Equal(255, image.MaxValue);
    }

    private static string WriteTemp(string name, string content)
    {
        string path = Path.Combine(Path.GetTempPath(), $"myPcdEditor_{Guid.NewGuid():N}_{name}");
        File.WriteAllText(path, content.ReplaceLineEndings("\n"));
        return path;
    }
}
