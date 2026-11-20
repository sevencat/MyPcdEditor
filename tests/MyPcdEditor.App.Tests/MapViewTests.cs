using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using MyPcdEditor.App.Controls;
using MyPcdEditor.Core;
using MyPcdEditor.Core.Grid;

namespace MyPcdEditor.App.Tests;

/// <summary>2D 地图视图在 headless（Skia）下能不能真的画出 occupancy 颜色。</summary>
public sealed class MapViewTests
{
    private static PgmImage CreateHalfOccupiedMap(int size = 64)
    {
        var pixels = new byte[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                pixels[(y * size) + x] = x < size / 2 ? (byte)0 : (byte)254;
            }
        }

        return new PgmImage { Width = size, Height = size, MaxValue = 255, Pixels = pixels };
    }

    [AvaloniaFact]
    public void MapView_RendersOccupiedAndFreeCells()
    {
        var map = new MapView { Width = 320, Height = 240 };
        var window = new Window { Width = 320, Height = 240, Content = map };
        window.Show();

        map.SetMap(
            CreateHalfOccupiedMap(),
            new MapYaml { Resolution = 0.1, OriginX = 0, OriginY = 0, OccupiedThresh = 0.65, FreeThresh = 0.196 });

        Dispatcher.UIThread.RunJobs();

        Assert.True(map.HasMap);
        Assert.True(map.PixelsPerMeter > 1, $"缩放异常：{map.PixelsPerMeter}");

        WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);

        (int light, int dark) = CountBrightAndDarkPixels(frame!);
        Assert.True(light > 500, $"占用色像素太少：{light}");
        Assert.True(dark > 500, $"空闲色像素太少：{dark}");

        window.Close();
    }

    /// <summary>
    /// 真实数据（存在才跑）：确认 30 M px 的 PGM 能画出来，并把快照落到 temp 便于肉眼核对。
    /// </summary>
    [AvaloniaFact]
    public void MapView_RealPackage_RendersWalls()
    {
        string? directory = FindSamplePackage("map-20261116-180314");
        if (directory is null)
        {
            return; // 未放置样例数据（.gitignore 排除）
        }

        MapPackage package = MapPackage.Open(directory);
        if (package.PgmPath is null || package.Yaml is null)
        {
            return;
        }

        var map = new MapView { Width = 640, Height = 480 };
        var window = new Window { Width = 640, Height = 480, Content = map };
        window.Show();

        map.SetMap(PgmReader.Read(package.PgmPath), package.Yaml);
        Dispatcher.UIThread.RunJobs();

        WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);

        (int light, int dark) = CountBrightAndDarkPixels(frame!);

        string path = Path.Combine(Path.GetTempPath(), "mypcd-mapview.png");
        frame!.Save(path, new PngBitmapEncoderOptions());
        Console.WriteLine($"[TEST] 2D 地图快照：{path}（亮 {light} / 暗 {dark}）");

        Assert.True(light > 200, $"墙体（占用）像素太少：{light}");
        Assert.True(dark > 5000, $"空闲像素太少：{dark}");

        window.Close();
    }

    /// <summary>
    /// 真实数据（存在才跑）：地面纹理数据本身必须含有墙，并落盘 PNG 供肉眼核对。
    /// </summary>
    [AvaloniaFact]
    public void GroundTexture_RealPackage_KeepsWalls()
    {
        string? directory = FindSamplePackage("map-20261116-180314");
        if (directory is null)
        {
            return;
        }

        MapPackage package = MapPackage.Open(directory);
        if (package.PgmPath is null || package.Yaml is null)
        {
            return;
        }

        PgmImage pgm = PgmReader.Read(package.PgmPath);

        var watch = Stopwatch.StartNew();
        GroundTexture? texture = GroundTexture.Build(pgm, package.Yaml);
        watch.Stop();

        Assert.NotNull(texture);
        Console.WriteLine($"[TEST] 地面纹理构建：{pgm.Width}×{pgm.Height} → " +
                          $"{texture!.Width}×{texture.Height}（块 {texture.BlockSize}）/ {watch.ElapsedMilliseconds} ms");
        Assert.True(watch.ElapsedMilliseconds < 2000, $"地面纹理构建太慢：{watch.ElapsedMilliseconds} ms");

        int occupied = texture!.CountOccupied();
        double ratio = occupied * 100.0 / (texture.Width * texture.Height);

        var bitmap = new WriteableBitmap(
            new PixelSize(texture.Width, texture.Height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Unpremul);

        var row = new byte[texture.Width * 4];
        using (ILockedFramebuffer buffer = bitmap.Lock())
        {
            for (var y = 0; y < texture.Height; y++)
            {
                for (var x = 0; x < texture.Width; x++)
                {
                    int src = ((y * texture.Width) + x) * 4;
                    row[x * 4] = texture.Rgba[src + 2];     // B
                    row[(x * 4) + 1] = texture.Rgba[src + 1]; // G
                    row[(x * 4) + 2] = texture.Rgba[src];     // R
                    row[(x * 4) + 3] = 255;
                }

                Marshal.Copy(row, 0, IntPtr.Add(buffer.Address, y * buffer.RowBytes), row.Length);
            }
        }

        string path = Path.Combine(Path.GetTempPath(), "mypcd-groundtexture.png");
        bitmap.Save(path, new PngBitmapEncoderOptions());
        Console.WriteLine($"[TEST] 地面纹理快照：{path}（{texture.Width}×{texture.Height}，墙 {ratio:F1}%）");

        Assert.True(ratio > 1.0, $"地面纹理里的墙太少：{ratio:F2}%");
    }

    /// <summary>向上找仓库根，再拼 <c>testdata/&lt;name&gt;</c>。</summary>
    private static string? FindSamplePackage(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MyPcdEditor.slnx")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            return null;
        }

        string candidate = Path.Combine(directory.FullName, "testdata", name);
        return Directory.Exists(candidate) ? candidate : null;
    }

    /// <summary>按 BGRA 读回，统计亮像素（占用）与暗像素（空闲）。</summary>
    private static (int Light, int Dark) CountBrightAndDarkPixels(WriteableBitmap bitmap)
    {
        int light = 0, dark = 0;
        byte[] row = new byte[bitmap.PixelSize.Width * 4];

        using (ILockedFramebuffer buffer = bitmap.Lock())
        {
            for (int y = 0; y < bitmap.PixelSize.Height; y += 4)
            {
                Marshal.Copy(IntPtr.Add(buffer.Address, y * buffer.RowBytes), row, 0, row.Length);
                for (int x = 0; x < bitmap.PixelSize.Width; x += 4)
                {
                    int luma = (row[(x * 4) + 2] + row[(x * 4) + 1] + row[x * 4]) / 3;
                    if (luma > 150)
                    {
                        light++;
                    }
                    else if (luma < 80)
                    {
                        dark++;
                    }
                }
            }
        }

        return (light, dark);
    }
}
