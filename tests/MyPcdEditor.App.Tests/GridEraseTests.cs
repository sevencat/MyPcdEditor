using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using MyPcdEditor.App.Controls;
using MyPcdEditor.App.Views;
using MyPcdEditor.Core;
using MyPcdEditor.Core.Grid;
using MyPcdEditor.Core.PointCloud;
using Xunit;

namespace MyPcdEditor.App.Tests;

/// <summary>2D 栅格擦除：地图上涂抹采样、落笔改像素、工具栏开关。</summary>
public sealed class GridEraseTests
{
    [AvaloniaFact]
    public void MapView_EraseMode_DragEmitsGridStrokeWithoutPanning()
    {
        var window = new Window { Width = 400, Height = 400 };
        var map = new MapView { BrushRadiusPixels = 16 };
        window.Content = map;
        window.Show();
        Dispatcher.UIThread.RunJobs();

        map.SetMap(MakeImage(), new MapYaml { Resolution = 0.05, OriginX = 0, OriginY = 0 });
        Dispatcher.UIThread.RunJobs();

        map.SetEraseMode(true);
        map.FitToMap(); // 先适配一次：不然首次渲染的适配会把中心改掉，和「涂抹是否拖动」混在一起
        Dispatcher.UIThread.RunJobs();
        (double centerX, double centerY) = (map.Transform.CenterX, map.Transform.CenterY);

        Vector2[]? path = null;
        var radius = 0.0;
        map.EraseCompleted += (p, r) =>
        {
            path = p;
            radius = r;
        };

        var center = new Point(map.Bounds.Width / 2, map.Bounds.Height / 2);
        window.MouseDown(center, MouseButton.Left);
        window.MouseMove(center + new Point(60, 0));
        window.MouseMove(center + new Point(120, 20));
        Dispatcher.UIThread.RunJobs();
        window.MouseUp(center + new Point(120, 20), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(path);
        Assert.True(path!.Length >= 2, "两次移动应该各采到一个顶点");
        Assert.True(radius > 0);
        // 擦除模式下左键是笔刷，不能顺手把地图拖走
        Assert.Equal(centerX, map.Transform.CenterX, 6);
        Assert.Equal(centerY, map.Transform.CenterY, 6);

        window.Close();
    }

    [AvaloniaFact]
    public void GridEraseToggle_SyncsWithMap()
    {
        MainWindow window = new() { Width = 480, Height = 360 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        try
        {
            var map = window.FindControl<MapView>("Map")!;
            var toggle = window.FindControl<ToggleButton>("GridEraseToggle")!;

            toggle.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(map.EraseMode);

            // G 键在地图上切换时要反过来把按钮弹起来
            map.SetEraseMode(false);
            Dispatcher.UIThread.RunJobs();
            Assert.False(toggle.IsChecked);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>把 (20,50) → (80,50) 这条横线擦掉：第一次改成未知，第二次「已经是未知」，再刷成空闲。</summary>
    [AvaloniaFact]
    public async Task ApplyGridErase_PaintsUnknownThenFree()
    {
        string directory = MakePackage();
        MainWindow window = new() { Width = 480, Height = 360 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        try
        {
            Assert.True(await window.LoadPackageAsync(directory));

            Vector2[] path = [new Vector2(20, 50), new Vector2(80, 50)];

            Assert.True(await window.ApplyGridEraseAsync(path, 3, OccupancyCell.Unknown));
            Assert.Contains("栅格擦除→未知", StatusOf(window));

            // 同一片再擦一遍：一个像素都没变
            Assert.False(await window.ApplyGridEraseAsync(path, 3, OccupancyCell.Unknown));
            Assert.Contains("已经是未知", StatusOf(window));

            Assert.True(await window.ApplyGridEraseAsync(path, 3, OccupancyCell.Free));
            Assert.Contains("栅格擦除→空闲", StatusOf(window));
        }
        finally
        {
            // 地面纹理在后台重建，等它跑完再关窗口
            await Task.Delay(200);
            Dispatcher.UIThread.RunJobs();
            window.Close();
            Cleanup(directory);
        }
    }

    private static string StatusOf(MainWindow window)
        => window.FindControl<TextBlock>("StatusText")!.Text ?? string.Empty;

    private static PgmImage MakeImage()
    {
        var pixels = new byte[100 * 100];
        Array.Fill(pixels, GridEraser.UnknownValue);
        for (var row = 40; row < 60; row++)
        {
            for (var col = 0; col < 100; col++)
            {
                pixels[(row * 100) + col] = 0;
            }
        }

        return new PgmImage { Width = 100, Height = 100, MaxValue = 255, Pixels = pixels };
    }

    /// <summary>造一个最小地图包：一撮点 + 100×100 栅格（中间 20 行占用）+ yaml。</summary>
    private static string MakePackage()
    {
        string directory = Path.Combine(Path.GetTempPath(), "pcd-grid-erase-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);

        var data = new byte[64 * 12];
        for (var i = 0; i < 64; i++)
        {
            BitConverter.TryWriteBytes(data.AsSpan(i * 12, 4), (float)(i % 8) * 0.5f);
            BitConverter.TryWriteBytes(data.AsSpan((i * 12) + 4, 4), (float)(i / 8) * 0.5f);
            BitConverter.TryWriteBytes(data.AsSpan((i * 12) + 8, 4), 0f);
        }

        using (XyzCloud cloud = XyzCloud.Create(data, 64))
        {
            PcdWriter.WriteBinaryXyz(Path.Combine(directory, "map.pcd"), cloud);
        }

        PgmWriter.Write(Path.Combine(directory, "map.pgm"), MakeImage());
        new MapYaml { Resolution = 0.05, OriginX = 0, OriginY = 0 }
            .Save(Path.Combine(directory, "map.yaml"), "map.pgm");

        return directory;
    }

    private static void Cleanup(string directory)
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
}
