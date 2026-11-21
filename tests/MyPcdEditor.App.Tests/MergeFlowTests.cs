using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MyPcdEditor.App.Views;
using MyPcdEditor.Core;
using MyPcdEditor.Core.Grid;
using MyPcdEditor.Core.PointCloud;
using Vector3 = System.Numerics.Vector3;

namespace MyPcdEditor.App.Tests;

/// <summary>
/// 合并入口的接线：选包 → 合并 → 报告确认 → 替换视图 → 导出成新包。
/// </summary>
/// <remarks>
/// 用锚点差精确等于场景平移的合成包，让配准初值就是真值（ICP 不动），
/// 这样断言能只盯着接线本身：点数、栅格 meta 有没有换成合并后的那份、导出用了哪个文件名。
/// </remarks>
public sealed class MergeFlowTests
{
    [AvaloniaFact]
    public async Task Merge_AdoptsResultAndExportsMergedPackage()
    {
        string root = Directory.CreateTempSubdirectory("pcd-merge-adopt").FullName;
        MainWindow? window = null;
        try
        {
            string referenceDirectory = MakePackage(Path.Combine(root, "ref"), 0.0);
            string sourceDirectory = MakePackage(Path.Combine(root, "src"), 5.0);

            window = CreateWindow();
            Assert.True(await window.LoadPackageAsync(referenceDirectory));

            window.ConfirmMergeReport = _ => Task.FromResult(true);
            Assert.True(await window.MergeAsync(sourceDirectory, null));
            Assert.Contains("已合并", window.Title);

            string exportDirectory = Path.Combine(root, "merged");
            await window.ExportAsync(exportDirectory);

            // 文件名参照当前打开的包（原包叫 map.pcd），而不是另起一个 merged.pcd
            string pcd = Path.Combine(exportDirectory, "map.pcd");
            Assert.True(File.Exists(pcd), "合并结果应导出成与参考包同名的 map.pcd");

            using XyzCloud merged = XyzCloud.Open(pcd);
            Assert.Equal(2_000, merged.PointCount);

            // 两个 5×5 m 的图在参考系里错开 5 m → 合并后 10 m 宽（200 px @0.05）
            PgmImage map = PgmReader.Read(Path.Combine(exportDirectory, "map.pgm"));
            Assert.InRange(map.Width, 190, 210);
            Assert.Equal(100, map.Height);

            // 锚点必须跟着走：否则导出的合并件再打开就没法参与下一次合并
            Assert.True(File.Exists(Path.Combine(exportDirectory, ".projected_origin")), "导出件应带 .projected_origin");
        }
        finally
        {
            window?.Close();
            TryDelete(root);
        }
    }

    [AvaloniaFact]
    public async Task Merge_Declined_KeepsCurrentPackage()
    {
        string root = Directory.CreateTempSubdirectory("pcd-merge-decline").FullName;
        MainWindow? window = null;
        try
        {
            string referenceDirectory = MakePackage(Path.Combine(root, "ref"), 0.0);
            string sourceDirectory = MakePackage(Path.Combine(root, "src"), 5.0);

            window = CreateWindow();
            Assert.True(await window.LoadPackageAsync(referenceDirectory));

            window.ConfirmMergeReport = _ => Task.FromResult(false);
            Assert.False(await window.MergeAsync(sourceDirectory, null));
            Assert.DoesNotContain("已合并", window.Title);

            // 放弃后导出的仍是原始包那一份（1000 点、5×5 m）
            string exportDirectory = Path.Combine(root, "kept");
            await window.ExportAsync(exportDirectory);

            using XyzCloud cloud = XyzCloud.Open(Path.Combine(exportDirectory, "map.pcd"));
            Assert.Equal(1_000, cloud.PointCount);
            Assert.Equal(100, PgmReader.Read(Path.Combine(exportDirectory, "map.pgm")).Width);
        }
        finally
        {
            window?.Close();
            TryDelete(root);
        }
    }

    /// <summary>
    /// 导出到当前打开的包目录会被拒绝：写一半就可能把原始数据毁掉，
    /// 覆盖原包是单独一步（「替换」，要备份 + 二次确认），不该混在导出里。
    /// </summary>
    [AvaloniaFact]
    public async Task Export_RefusesToOverwriteCurrentPackage()
    {
        string root = Directory.CreateTempSubdirectory("pcd-export-self").FullName;
        MainWindow? window = null;
        try
        {
            string referenceDirectory = MakePackage(Path.Combine(root, "ref"), 0.0);

            window = CreateWindow();
            Assert.True(await window.LoadPackageAsync(referenceDirectory));

            await window.ExportAsync(referenceDirectory);

            string status = window.FindControl<TextBlock>("StatusText")!.Text!;
            Assert.Contains("替换", status);

            using XyzCloud cloud = XyzCloud.Open(Path.Combine(referenceDirectory, "map.pcd"));
            Assert.Equal(1_000, cloud.PointCount);
        }
        finally
        {
            window?.Close();
            TryDelete(root);
        }
    }

    /// <summary>
    /// 替换：把合并结果写回参考包目录。这是唯一会改动原始文件的动作，
    /// 所以除了「写进去了」，还要断言旧文件被改名备份、备份里是原来的内容。
    /// </summary>
    [AvaloniaFact]
    public async Task Replace_WritesMergedResultBackToReferencePackage()
    {
        string root = Directory.CreateTempSubdirectory("pcd-replace").FullName;
        MainWindow? window = null;
        try
        {
            string referenceDirectory = MakePackage(Path.Combine(root, "ref"), 0.0);
            string sourceDirectory = MakePackage(Path.Combine(root, "src"), 5.0);

            window = CreateWindow();
            window.ConfirmMergeReport = _ => Task.FromResult(true);
            window.ConfirmReplace = _ => Task.FromResult(true);

            Assert.True(await window.LoadPackageAsync(referenceDirectory));
            Assert.True(await window.MergeAsync(sourceDirectory, null));

            await window.ReplaceAsync();

            // 先看状态栏：替换失败时它会带上原因，比「点数不对」好定位
            Assert.Contains("已替换", window.FindControl<TextBlock>("StatusText")!.Text);

            using (XyzCloud written = XyzCloud.Open(Path.Combine(referenceDirectory, "map.pcd")))
            {
                Assert.Equal(2_000, written.PointCount);
            }

            // 旧文件改成 .bak 留着：替换失败或后悔时能拿回来
            using (XyzCloud backup = XyzCloud.Open(Path.Combine(referenceDirectory, "map.pcd.bak")))
            {
                Assert.Equal(1_000, backup.PointCount);
            }

            // 栅格也写回去了（合并图比原图宽），锚点与包结构不变
            PgmImage map = PgmReader.Read(Path.Combine(referenceDirectory, "map.pgm"));
            Assert.InRange(map.Width, 190, 210);
            Assert.NotNull(MapPackage.Open(referenceDirectory).ProjectedOrigin);

            Assert.Contains("已替换", window.FindControl<TextBlock>("StatusText")!.Text);
        }
        finally
        {
            window?.Close();
            TryDelete(root);
        }
    }

    /// <summary>确认框里点「取消」：原包一个字节都不动。</summary>
    [AvaloniaFact]
    public async Task Replace_Declined_LeavesPackageUntouched()
    {
        string root = Directory.CreateTempSubdirectory("pcd-replace-decline").FullName;
        MainWindow? window = null;
        try
        {
            string referenceDirectory = MakePackage(Path.Combine(root, "ref"), 0.0);
            string sourceDirectory = MakePackage(Path.Combine(root, "src"), 5.0);

            window = CreateWindow();
            window.ConfirmMergeReport = _ => Task.FromResult(true);
            window.ConfirmReplace = _ => Task.FromResult(false);

            Assert.True(await window.LoadPackageAsync(referenceDirectory));
            Assert.True(await window.MergeAsync(sourceDirectory, null));

            await window.ReplaceAsync();

            using (XyzCloud kept = XyzCloud.Open(Path.Combine(referenceDirectory, "map.pcd")))
            {
                Assert.Equal(1_000, kept.PointCount);
            }

            Assert.False(File.Exists(Path.Combine(referenceDirectory, "map.pcd.bak")));
            Assert.Contains("已取消", window.FindControl<TextBlock>("StatusText")!.Text);
        }
        finally
        {
            window?.Close();
            TryDelete(root);
        }
    }

    /// <summary>
    /// 打开后没动过点云（零拷贝，文件还被映射着）就替换：pcd 跳过不写，
    /// 只写栅格与 yaml。硬写会因为文件占用失败，跳过才是正确行为。
    /// </summary>
    [AvaloniaFact]
    public async Task Replace_SkipsPcdWhenCloudIsUnchanged()
    {
        string root = Directory.CreateTempSubdirectory("pcd-replace-skip").FullName;
        MainWindow? window = null;
        try
        {
            string referenceDirectory = MakePackage(Path.Combine(root, "ref"), 0.0);

            window = CreateWindow();
            window.ConfirmReplace = _ => Task.FromResult(true);

            Assert.True(await window.LoadPackageAsync(referenceDirectory));

            await window.ReplaceAsync();

            Assert.False(File.Exists(Path.Combine(referenceDirectory, "map.pcd.bak")));
            Assert.True(File.Exists(Path.Combine(referenceDirectory, "map.pgm.bak")));
            Assert.Contains("未重写 pcd", window.FindControl<TextBlock>("StatusText")!.Text);

            using XyzCloud cloud = XyzCloud.Open(Path.Combine(referenceDirectory, "map.pcd"));
            Assert.Equal(1_000, cloud.PointCount);
        }
        finally
        {
            window?.Close();
            TryDelete(root);
        }
    }

    private static MainWindow CreateWindow()
    {
        var window = new MainWindow { Width = 480, Height = 360 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    /// <summary>
    /// 清理临时目录。点云是零拷贝 MMF，视图没换云之前文件一直被映射着，
    /// headless 里也不会再渲染一帧来释放，所以删不掉就留着（失败也只影响临时目录）。
    /// </summary>
    private static void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// 造一个最小地图包：点云 + 栅格 + yaml + .projected_origin。
    /// <paramref name="offset"/> 既是场景在参考系里的东移量，也是锚点差。
    /// </summary>
    private static string MakePackage(string directory, double offset)
    {
        Directory.CreateDirectory(directory);

        var random = new Random(20261121);
        var points = new Vector3[1_000];
        for (var i = 0; i < points.Length; i++)
        {
            points[i] = new Vector3(
                (float)offset + ((float)random.NextDouble() * 5f),
                (float)random.NextDouble() * 5f,
                (float)random.NextDouble() * 3f);
        }

        using (XyzCloud cloud = MakeCloud(points))
        {
            PcdWriter.WriteBinaryXyz(Path.Combine(directory, "map.pcd"), cloud);
        }

        var pixels = new byte[100 * 100];
        Array.Fill(pixels, (byte)205);
        for (var row = 40; row < 60; row++)
        {
            for (var col = 0; col < 100; col++)
            {
                pixels[(row * 100) + col] = 0;
            }
        }

        PgmWriter.Write(Path.Combine(directory, "map.pgm"),
            new PgmImage { Width = 100, Height = 100, MaxValue = 255, Pixels = pixels });

        new MapYaml { Resolution = 0.05, OriginX = 0, OriginY = 0 }
            .Save(Path.Combine(directory, "map.yaml"), "map.pgm");

        File.WriteAllText(
            Path.Combine(directory, ".projected_origin"),
            string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"origin: [{342000 + offset}, 3432000, 14]\nzone: 21\n"));

        return directory;
    }

    private static XyzCloud MakeCloud(Vector3[] points)
    {
        var data = new byte[points.Length * 12];
        for (var i = 0; i < points.Length; i++)
        {
            BitConverter.TryWriteBytes(data.AsSpan(i * 12, 4), points[i].X);
            BitConverter.TryWriteBytes(data.AsSpan((i * 12) + 4, 4), points[i].Y);
            BitConverter.TryWriteBytes(data.AsSpan((i * 12) + 8, 4), points[i].Z);
        }

        return XyzCloud.Create(data, points.Length);
    }
}
