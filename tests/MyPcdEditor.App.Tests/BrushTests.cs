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
using Xunit;

namespace MyPcdEditor.App.Tests;

/// <summary>3D 笔刷：模式互斥、拖动采集笔迹、松手删点。</summary>
public sealed class BrushTests
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

    /// <summary>框选和笔刷抢同一个左键：只能开一个，否则拖一下既画框又画笔迹。</summary>
    [AvaloniaFact]
    public void BrushAndBoxSelect_AreMutuallyExclusive()
    {
        MainWindow window = CreateWindow();
        try
        {
            var view = window.FindControl<PointCloudView>("View")!;
            var brush = window.FindControl<ToggleButton>("BrushToggle")!;
            var box = window.FindControl<ToggleButton>("BoxSelectToggle")!;

            brush.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(view.BrushMode);
            Assert.False(view.BoxSelectMode);

            box.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(view.BoxSelectMode);
            Assert.False(view.BrushMode);
            Assert.False(brush.IsChecked, "切到框选时笔刷按钮要跟着弹起");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void BrushMode_DragCollectsStrokeAndKeepsCamera()
    {
        MainWindow window = CreateWindow();
        try
        {
            var view = window.FindControl<PointCloudView>("View")!;
            window.FindControl<ToggleButton>("BrushToggle")!.IsChecked = true;
            Dispatcher.UIThread.RunJobs();

            float yawBefore = view.Camera.Yaw;
            Vector2[]? completed = null;
            var radius = 0.0;
            view.BrushCompleted += (path, r) =>
            {
                completed = path;
                radius = r;
            };

            Point center = CenterOf(view, window);
            window.MouseDown(center, MouseButton.Left);
            window.MouseMove(center + new Point(30, 10));
            window.MouseMove(center + new Point(70, 30));
            Dispatcher.UIThread.RunJobs();
            window.MouseUp(center + new Point(70, 30), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            Assert.NotNull(completed);
            Assert.True(completed!.Length >= 2, "两次移动应该各采到一个顶点");
            Assert.True(radius > 0);
            Assert.Equal(yawBefore, view.Camera.Yaw);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>单位阵投影 + 方形视口：x&lt;0 在屏幕左半、x&gt;0 在右半，答案确定。</summary>
    [AvaloniaFact]
    public async Task ApplyBrush_RemovesPointsUnderStroke()
    {
        MainWindow window = CreateWindow();
        try
        {
            var view = window.FindControl<PointCloudView>("View")!;
            view.SetCloud(MakeCloud(
                new Vector3(-0.5f, 0, 0),
                new Vector3(0f, 0, 0),
                new Vector3(0.75f, 0, 0)));

            bool changed = await window.ApplyBrushAsync(
                [new Vector2(0.5f, 0.5f), new Vector2(1f, 0.5f)], 0.05f,
                Matrix4x4.Identity, 1f);

            Assert.True(changed);
            Assert.Equal(1, view.CurrentCloud!.PointCount);
            Assert.Contains("笔刷擦除", window.FindControl<TextBlock>("StatusText")!.Text);
        }
        finally
        {
            window.Close();
        }
    }
}
