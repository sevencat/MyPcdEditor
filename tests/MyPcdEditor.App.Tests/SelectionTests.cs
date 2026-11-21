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
using MyPcdEditor.Core.PointCloud;
using Vector3 = System.Numerics.Vector3;

namespace MyPcdEditor.App.Tests;

/// <summary>3D 框选：手势（画框不动相机）与落地（删掉框内点）。</summary>
public sealed class SelectionTests
{
    private static MainWindow CreateWindow()
    {
        var window = new MainWindow { Width = 480, Height = 360 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static Point CenterOf(Control control, Visual root)
        => control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), root)
           ?? new Point(0, 0);

    private static XyzCloud MakeCloud(params Vector3[] points)
    {
        var data = new byte[points.Length * XyzCloud.Stride];
        for (var i = 0; i < points.Length; i++)
        {
            BitConverter.TryWriteBytes(data.AsSpan(i * XyzCloud.Stride, 4), points[i].X);
            BitConverter.TryWriteBytes(data.AsSpan((i * XyzCloud.Stride) + 4, 4), points[i].Y);
            BitConverter.TryWriteBytes(data.AsSpan((i * XyzCloud.Stride) + 8, 4), points[i].Z);
        }

        return XyzCloud.Create(data, points.Length);
    }

    [AvaloniaFact]
    public void BoxSelectMode_DragDrawsBoxAndKeepsCamera()
    {
        MainWindow window = CreateWindow();
        try
        {
            var view = window.FindControl<PointCloudView>("View")!;
            var toggle = window.FindControl<ToggleButton>("BoxSelectToggle")!;

            toggle.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(view.BoxSelectMode, "工具栏开关应打开视图的框选模式");

            float yawBefore = view.Camera.Yaw;
            ScreenBox? selected = null;
            var keepInside = true;
            view.BoxSelected += (box, keep) =>
            {
                selected = box;
                keepInside = keep;
            };

            Point center = CenterOf(view, window);
            window.MouseDown(center, MouseButton.Left);
            window.MouseMove(center + new Point(80, 60));
            Dispatcher.UIThread.RunJobs();
            window.MouseUp(center + new Point(80, 60), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            Assert.NotNull(selected);
            Assert.False(keepInside, "没按 Shift 就是删掉框内");
            Assert.True(selected!.Value.Width > 0 && selected.Value.Height > 0);
            Assert.Equal(yawBefore, view.Camera.Yaw);
            Assert.NotNull(view.Selection);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>几像素的抖动不能当框：否则点一下就把脚下那片删了。</summary>
    [AvaloniaFact]
    public void BoxSelectMode_TinyDragIsIgnored()
    {
        MainWindow window = CreateWindow();
        try
        {
            var view = window.FindControl<PointCloudView>("View")!;
            window.FindControl<ToggleButton>("BoxSelectToggle")!.IsChecked = true;
            Dispatcher.UIThread.RunJobs();

            var fired = 0;
            view.BoxSelected += (_, _) => fired++;
            Point center = CenterOf(view, window);

            window.MouseDown(center, MouseButton.Left);
            window.MouseMove(center + new Point(1, 1));
            window.MouseUp(center + new Point(1, 1), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(0, fired);
            Assert.Null(view.Selection);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>单位阵投影下 x&lt;0 在屏幕左半、x&gt;0 在右半，于是「框右半」有确定答案。</summary>
    [AvaloniaFact]
    public async Task ApplySelection_RemovesPointsInsideBox()
    {
        MainWindow window = CreateWindow();
        try
        {
            var view = window.FindControl<PointCloudView>("View")!;
            view.SetCloud(MakeCloud(
                new Vector3(-0.5f, 0, 0),
                new Vector3(-0.2f, 0.1f, 0),
                new Vector3(0.3f, 0, 0),
                new Vector3(0.8f, -0.2f, 0)));

            bool changed = await window.ApplySelectionAsync(
                new ScreenBox(0.5f, 0f, 1f, 1f), keepInside: false, Matrix4x4.Identity);

            Assert.True(changed);
            Assert.Equal(2, view.CurrentCloud!.PointCount);
            Assert.Contains("框选删除", window.FindControl<TextBlock>("StatusText")!.Text);
            Assert.Null(view.Selection);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>框太大把点全删光时宁可不动：那等于「删掉整张地图」，多半是手滑。</summary>
    [AvaloniaFact]
    public async Task ApplySelection_AllPoints_IsDeclined()
    {
        MainWindow window = CreateWindow();
        try
        {
            var view = window.FindControl<PointCloudView>("View")!;
            view.SetCloud(MakeCloud(new Vector3(-0.5f, 0, 0), new Vector3(0.5f, 0, 0)));

            bool changed = await window.ApplySelectionAsync(
                new ScreenBox(0f, 0f, 1f, 1f), keepInside: false, Matrix4x4.Identity);

            Assert.False(changed);
            Assert.Equal(2, view.CurrentCloud!.PointCount);
            Assert.Contains("全删光", window.FindControl<TextBlock>("StatusText")!.Text);
        }
        finally
        {
            window.Close();
        }
    }
}
