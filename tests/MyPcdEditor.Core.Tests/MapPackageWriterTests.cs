using System.Globalization;
using System.Numerics;
using MyPcdEditor.Core.Grid;
using MyPcdEditor.Core.PointCloud;

namespace MyPcdEditor.Core.Tests;

/// <summary>
/// 写完整地图包：文件名 / 锚点 / toml 都参照当前打开的包，
/// 写出来的目录要能被 <see cref="MapPackage.Open"/> 原样读回（否则合并结果没法再参与合并）。
/// </summary>
public sealed class MapPackageWriterTests
{
    [Fact]
    public void Write_ProducesPackageThatOpensBack()
    {
        string root = TempRoot();
        string referenceDir = Path.Combine(root, "ref");
        string exportDir = Path.Combine(root, "out");

        try
        {
            MapPackage reference = MakePackage(referenceDir, "map.pcd", withAnchor: true, withIndex: true);

            using XyzCloud cloud = MakeCloud(6);
            var grid = new PgmImage { Width = 4, Height = 3, MaxValue = 255, Pixels = new byte[12] };
            var meta = new MapYaml { Resolution = 0.05, OriginX = -1.5, OriginY = 2.25 };

            MapPackageWriteResult result = MapPackageWriter.Write(exportDir, reference, cloud, grid, meta);

            Assert.True(result.HasProjectedOrigin);
            Assert.True(result.HasIndex);
            Assert.Contains("map.pcd", result.Files);
            Assert.Contains("map.pgm", result.Files);
            Assert.Contains("map.yaml", result.Files);
            Assert.Contains(".projected_origin", result.Files);

            MapPackage reopened = MapPackage.Open(exportDir);

            using (XyzCloud written = XyzCloud.Open(reopened.PcdPath!))
            {
                Assert.Equal(6, written.PointCount);
            }

            Assert.Equal(4, PgmReader.Read(reopened.PgmPath!).Width);
            Assert.Equal(0.05, reopened.Yaml!.Resolution, 9);
            Assert.Equal(-1.5, reopened.Yaml.OriginX, 9);

            // 锚点带过去了：合并结果的局部系就是参考包的局部系，不带锚点就没法再合并
            Assert.NotNull(reopened.ProjectedOrigin);
            Assert.Equal(342416.112, reopened.ProjectedOrigin.X, 3);
            Assert.Equal(21, reopened.ProjectedOrigin.Zone);

            // yaml 的 image 改写成同目录文件名（原包写的是另一台机器的绝对路径）
            Assert.Equal("map.pgm", reopened.Yaml.Image);

            Assert.NotNull(reopened.Index);
            Assert.Equal("map", Assert.Single(reopened.Index!.Grids).Name);

            // toml 整份照抄，path 也照抄：下游按 name 找同目录文件，不靠 path；
            // 改写成导出目录的 Windows 路径对 Linux 侧毫无意义，还会丢掉采集机上的原值
            Assert.Equal("/home/ysc/map", reopened.Index.Path);
            Assert.Equal(
                File.ReadAllBytes(reference.IndexPath!),
                File.ReadAllBytes(reopened.IndexPath!));
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    public void Write_KeepsReferenceFileNames()
    {
        string root = TempRoot();
        string referenceDir = Path.Combine(root, "ref");
        string exportDir = Path.Combine(root, "out");

        try
        {
            MapPackage reference = MakePackage(referenceDir, "cloud.pcd", withAnchor: false, withIndex: false);

            using XyzCloud cloud = MakeCloud(3);
            MapPackageWriteResult result = MapPackageWriter.Write(exportDir, reference, cloud, null, null);

            // 参照原目录：原包叫 cloud.pcd，导出件也叫 cloud.pcd，而不是换个名字
            Assert.Equal("cloud.pcd", Assert.Single(result.Files));
            Assert.False(result.HasProjectedOrigin);
            Assert.False(File.Exists(Path.Combine(exportDir, ".projected_origin")));
            Assert.False(File.Exists(Path.Combine(exportDir, "map.yaml")));
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    public void Write_RefusesToOverwriteWhenDisallowed()
    {
        string root = TempRoot();
        string referenceDir = Path.Combine(root, "ref");
        string exportDir = Path.Combine(root, "out");

        try
        {
            MapPackage reference = MakePackage(referenceDir, "map.pcd", withAnchor: false, withIndex: false);
            Directory.CreateDirectory(exportDir);

            string existing = Path.Combine(exportDir, "map.pcd");
            File.WriteAllText(existing, "旧包，别动我");

            using XyzCloud cloud = MakeCloud(3);

            Assert.Throws<IOException>(() =>
                MapPackageWriter.Write(exportDir, reference, cloud, null, null, overwrite: false));

            // 拒绝覆盖时旧文件必须原样（先写 .tmp 再改名就是为了这个）
            Assert.Equal("旧包，别动我", File.ReadAllText(existing));
            Assert.False(File.Exists(existing + ".tmp"));
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    public void Write_WithNothingToWrite_DoesNotCreateDirectory()
    {
        string root = TempRoot();
        string referenceDir = Path.Combine(root, "ref");
        string exportDir = Path.Combine(root, "out");

        try
        {
            MapPackage reference = MakePackage(referenceDir, "map.pcd", withAnchor: false, withIndex: false);

            MapPackageWriteResult result = MapPackageWriter.Write(exportDir, reference, null, null, null);

            Assert.Empty(result.Files);
            Assert.False(Directory.Exists(exportDir));
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>
    /// 替换：写回原目录、原文件改名 <c>.bak</c>。要断言的是「备份里是替换前的内容」，
    /// 只断言「目标变了」会漏掉备份写错（备份成新内容就白备份了）。
    /// </summary>
    [Fact]
    public void Replace_OverwritesInPlaceAndKeepsBackup()
    {
        string root = TempRoot();
        string referenceDir = Path.Combine(root, "ref");

        try
        {
            MapPackage reference = MakePackage(referenceDir, "map.pcd", withAnchor: true, withIndex: true);
            using XyzCloud bigger = MakeCloud(9);

            MapPackageWriteResult result = MapPackageWriter.Replace(referenceDir, reference, bigger, null, null);

            // 锚点与 toml 也整份重写（内容不变），所以也是一份备份
            Assert.Contains("map.pcd", result.Files);
            Assert.Contains(".projected_origin", result.Files);
            Assert.Contains("map.pcd.bak", result.Backups);

            using (XyzCloud written = XyzCloud.Open(Path.Combine(referenceDir, "map.pcd")))
            {
                Assert.Equal(9, written.PointCount);
            }

            // 备份里必须是替换前的内容（6 点），否则「后悔了能拿回来」是空话
            using (XyzCloud backup = XyzCloud.Open(Path.Combine(referenceDir, "map.pcd.bak")))
            {
                Assert.Equal(6, backup.PointCount);
            }

            Assert.NotNull(MapPackage.Open(referenceDir).ProjectedOrigin);
            Assert.False(File.Exists(Path.Combine(referenceDir, "map.pcd.tmp")));
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>
    /// 目标文件被占用（这里锁住 yaml）时，已经改成 .bak 的 pcd 必须还原回去：
    /// 一半新一半旧的地图包比没替换更糟。
    /// </summary>
    [Fact]
    public void Replace_RollsBackWhenTargetIsLocked()
    {
        string root = TempRoot();
        string referenceDir = Path.Combine(root, "ref");

        try
        {
            MapPackage reference = MakePackage(referenceDir, "map.pcd", withAnchor: true, withIndex: false);
            string yamlPath = Path.Combine(referenceDir, "map.yaml");
            byte[] yamlBefore = File.ReadAllBytes(yamlPath);

            using XyzCloud bigger = MakeCloud(9);
            var grid = new PgmImage { Width = 8, Height = 6, MaxValue = 255, Pixels = new byte[48] };
            var meta = new MapYaml { Resolution = 0.05 };

            // 共享读但不允许改名：pcd / pgm 会先让位，轮到 yaml 时失败
            using (new FileStream(yamlPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.Throws<IOException>(() =>
                    MapPackageWriter.Replace(referenceDir, reference, bigger, grid, meta));
            }

            using (XyzCloud kept = XyzCloud.Open(Path.Combine(referenceDir, "map.pcd")))
            {
                Assert.Equal(6, kept.PointCount);
            }

            Assert.Equal(4, PgmReader.Read(Path.Combine(referenceDir, "map.pgm")).Width);
            Assert.Equal(yamlBefore, File.ReadAllBytes(yamlPath));
            Assert.False(File.Exists(yamlPath + ".bak"));
            Assert.False(File.Exists(Path.Combine(referenceDir, "map.pcd.bak")));
            Assert.False(File.Exists(Path.Combine(referenceDir, "map.pgm.bak")));
            Assert.False(File.Exists(Path.Combine(referenceDir, "map.pcd.tmp")));
            Assert.False(File.Exists(Path.Combine(referenceDir, "map.pgm.tmp")));
            Assert.False(File.Exists(yamlPath + ".tmp"));
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>造一个最小参考包：pcd + pgm + yaml（+ 可选锚点与 toml）。</summary>
    private static MapPackage MakePackage(string directory, string pcdName, bool withAnchor, bool withIndex)
    {
        Directory.CreateDirectory(directory);

        using (XyzCloud cloud = MakeCloud(6))
        {
            PcdWriter.WriteBinaryXyz(Path.Combine(directory, pcdName), cloud);
        }

        string stem = Path.GetFileNameWithoutExtension(pcdName);
        var pixels = new byte[12];
        Array.Fill(pixels, (byte)205);
        PgmWriter.Write(Path.Combine(directory, stem + ".pgm"),
            new PgmImage { Width = 4, Height = 3, MaxValue = 255, Pixels = pixels });
        new MapYaml { Resolution = 0.1 }.Save(Path.Combine(directory, stem + ".yaml"), stem + ".pgm");

        if (withAnchor)
        {
            File.WriteAllText(Path.Combine(directory, ".projected_origin"), string.Create(
                CultureInfo.InvariantCulture,
                $"# projected origin for test\norigin: [342416.112, 3432910.256, 13.864]\nzone: 21\n"));
        }

        if (withIndex)
        {
            File.WriteAllText(Path.Combine(directory, "jueying_id_map.toml"),
                "path = \"/home/ysc/map\"\n\n[[grids]]\nid = 1\nname = \"map\"\n");
        }

        return MapPackage.Open(directory);
    }

    private static XyzCloud MakeCloud(int count)
    {
        var data = new byte[count * XyzCloud.Stride];
        for (var i = 0; i < count; i++)
        {
            var point = new Vector3(i, i * 0.5f, -i);
            BitConverter.TryWriteBytes(data.AsSpan(i * XyzCloud.Stride, 4), point.X);
            BitConverter.TryWriteBytes(data.AsSpan((i * XyzCloud.Stride) + 4, 4), point.Y);
            BitConverter.TryWriteBytes(data.AsSpan((i * XyzCloud.Stride) + 8, 4), point.Z);
        }

        return XyzCloud.Create(data, count);
    }

    private static string TempRoot() => Path.Combine(Path.GetTempPath(), "pcdwriter-" + Guid.NewGuid().ToString("N"));

    private static void Delete(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }
    }
}
