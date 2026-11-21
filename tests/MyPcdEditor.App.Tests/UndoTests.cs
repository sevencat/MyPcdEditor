using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MyPcdEditor.App.Controls;
using MyPcdEditor.App.Views;
using MyPcdEditor.Core;
using MyPcdEditor.Core.Grid;
using MyPcdEditor.Core.PointCloud;
using Xunit;

namespace MyPcdEditor.App.Tests;

/// <summary>撤销栈：编辑后 Ctrl+Z 能把点云/栅格换回改动前的那份。</summary>
public sealed class UndoTests
{
    /// <summary>单位阵投影下 x&lt;0 在左半屏、x&gt;0 在右半屏，「框右半」有确定答案。</summary>
    [AvaloniaFact]
    public async Task SelectionEdit_CanBeUndone()
    {
        MainWindow window = new() { Width = 480, Height = 360 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        try
        {
            var view = window.FindControl<PointCloudView>("View")!;
            var undoButton = window.FindControl<Button>("UndoButton")!;

            view.SetCloud(MakeCloud(
                new Vector3(-0.5f, 0, 0),
                new Vector3(-0.2f, 0.1f, 0),
                new Vector3(0.3f, 0, 0),
                new Vector3(0.8f, -0.2f, 0)));
            Dispatcher.UIThread.RunJobs();
            Assert.False(undoButton.IsEnabled);

            Assert.True(await window.ApplySelectionAsync(
                new ScreenBox(0.5f, 0f, 1f, 1f), keepInside: false, Matrix4x4.Identity));

            Assert.Equal(2, view.CurrentCloud!.PointCount);
            Assert.True(undoButton.IsEnabled, "编辑之后撤销按钮该亮起来");
            Assert.Contains("框选删除", undoButton.Content?.ToString());

            Assert.True(await window.UndoAsync());

            // 撤销要把原来那份云交回视图：点数、状态栏都得回到改之前
            Assert.Equal(4, view.CurrentCloud!.PointCount);
            Assert.Contains("已撤销", StatusOf(window));
            Assert.False(undoButton.IsEnabled, "栈空了按钮就该灭");

            // 再撤一次：没东西可撤了
            Assert.False(await window.UndoAsync());
            Assert.Contains("没有可撤销的编辑", StatusOf(window));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>栅格擦除不进点云那条路：撤销只换回栅格图，点云不动。</summary>
    [AvaloniaFact]
    public async Task GridErase_CanBeUndone()
    {
        string directory = MakePackage();
        MainWindow window = new() { Width = 480, Height = 360 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        try
        {
            Assert.True(await window.LoadPackageAsync(directory));

            // 第 50 行原本是占用（0），笔迹中心落在那里
            var row = 50;
            var column = 50;
            Assert.Equal(0, window.CurrentGrid!.Pixels[(row * 100) + column]);

            Assert.True(await window.ApplyGridEraseAsync(
                [new Vector2(column, row)], radiusGrid: 3, OccupancyCell.Unknown));
            Assert.Equal(GridEraser.UnknownValue, window.CurrentGrid!.Pixels[(row * 100) + column]);

            Assert.True(await window.UndoAsync());

            Assert.Equal(0, window.CurrentGrid!.Pixels[(row * 100) + column]);
            Assert.Contains("已撤销", StatusOf(window));
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

    private static XyzCloud MakeCloud(params Vector3[] points)
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

    /// <summary>最小地图包：一撮点 + 100×100 栅格（第 40~59 行占用）+ yaml。</summary>
    private static string MakePackage()
    {
        string directory = Path.Combine(Path.GetTempPath(), "pcd-undo-" + Guid.NewGuid().ToString("N")[..8]);
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
