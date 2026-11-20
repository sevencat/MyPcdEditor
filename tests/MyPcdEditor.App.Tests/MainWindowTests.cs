using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using MyPcdEditor.App.Controls;
using MyPcdEditor.App.Views;
using MyPcdEditor.Core.PointCloud;
using MyPcdEditor.Rendering.Gles3;
using Vector3 = System.Numerics.Vector3;

namespace MyPcdEditor.App.Tests;

/// <summary>
/// 验证「不依赖 GL 上下文」的部分能不能在 headless 上跑：窗口装配、命中测试、输入桥接、UI 绑定。
/// </summary>
public sealed class MainWindowTests
{
    private static MainWindow CreateWindow()
    {
        var window = new MainWindow { Width = 480, Height = 360 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    /// <summary>控件中心点换算到窗口坐标（headless 的鼠标事件用窗口坐标）。</summary>
    private static Point CenterOf(Control control, Visual root)
        => control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), root)
           ?? new Point(0, 0);

    [AvaloniaFact]
    public void Window_Assembles_WithoutGlContext()
    {
        MainWindow window = CreateWindow();
        try
        {
            var view = window.FindControl<PointCloudView>("View");

            Assert.NotNull(view);
            Assert.True(view.Bounds.Width > 0, "GL 控件应参与布局，即使拿不到 GL 上下文");
            Assert.True(window.FindControl<Button>("OpenButton")!.Bounds.Width > 0);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void LeftDrag_OrbitsCamera()
    {
        MainWindow window = CreateWindow();
        try
        {
            var view = window.FindControl<PointCloudView>("View")!;
            float yawBefore = view.Camera.Yaw;
            Point center = CenterOf(view, window);

            window.MouseDown(center, MouseButton.Left);
            window.MouseMove(center + new Point(60, 0));
            window.MouseUp(center + new Point(60, 0), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            Assert.NotEqual(yawBefore, view.Camera.Yaw);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Wheel_ChangesCameraDistance()
    {
        MainWindow window = CreateWindow();
        try
        {
            var view = window.FindControl<PointCloudView>("View")!;
            float distanceBefore = view.Camera.Distance;

            window.MouseWheel(CenterOf(view, window), new Vector(0, 1));
            Dispatcher.UIThread.RunJobs();

            Assert.True(view.Camera.Distance < distanceBefore, "向前滚应拉近相机");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void PointSizeSlider_PropagatesToView()
    {
        MainWindow window = CreateWindow();
        try
        {
            var view = window.FindControl<PointCloudView>("View")!;
            var slider = window.FindControl<Slider>("PointSizeSlider")!;

            slider.Value = 6;
            Dispatcher.UIThread.RunJobs();

            Assert.True(Math.Abs(view.PointSize - 6f) < 0.001f, $"点大小应同步为 6，实际 {view.PointSize}");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void DefaultView_IsOblique45Degrees()
    {
        MainWindow window = CreateWindow();
        try
        {
            OrbitCamera camera = window.FindControl<PointCloudView>("View")!.Camera;

            Assert.True(
                Math.Abs(camera.Yaw - OrbitCamera.DefaultYaw) < 1e-4f,
                $"默认水平角应为 -45°，实际 {camera.Yaw * 180f / MathF.PI:F1}°");
            Assert.True(
                Math.Abs(camera.Pitch - OrbitCamera.DefaultPitch) < 1e-4f,
                $"默认俯仰角应为 45°，实际 {camera.Pitch * 180f / MathF.PI:F1}°");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void LargeDrag_StaysWithinAdjustmentRange()
    {
        MainWindow window = CreateWindow();
        try
        {
            var view = window.FindControl<PointCloudView>("View")!;
            Point center = CenterOf(view, window);

            // 拖到远超视口的位置，模拟"想大幅转"的动作
            window.MouseDown(center, MouseButton.Left);
            window.MouseMove(center + new Point(2000, 2000));
            window.MouseUp(center + new Point(2000, 2000), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            OrbitCamera camera = view.Camera;
            Assert.True(
                camera.Yaw >= OrbitCamera.DefaultYaw - OrbitCamera.YawAllowance - 1e-3f &&
                camera.Yaw <= OrbitCamera.DefaultYaw + OrbitCamera.YawAllowance + 1e-3f,
                $"水平角应被限制在 ±25° 内，实际 {camera.Yaw * 180f / MathF.PI:F1}°");
            Assert.True(
                camera.Pitch >= OrbitCamera.DefaultPitch - OrbitCamera.PitchAllowance - 1e-3f &&
                camera.Pitch <= OrbitCamera.DefaultPitch + OrbitCamera.PitchAllowance + 1e-3f,
                $"俯仰角应被限制在 ±20° 内，实际 {camera.Pitch * 180f / MathF.PI:F1}°");
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public void Fit_CentersCameraOnCloudBounds()
    {
        var camera = new OrbitCamera();
        var bounds = new Bounds3(new Vector3(10f, 20f, -30f), new Vector3(30f, 60f, 10f));

        camera.Fit(bounds);

        Assert.True(Math.Abs(camera.Target.X - bounds.Center.X) < 1e-4f, $"Target.X 应等于中心，实际 {camera.Target.X}");
        Assert.True(Math.Abs(camera.Target.Y - bounds.Center.Y) < 1e-4f, $"Target.Y 应等于中心，实际 {camera.Target.Y}");
        Assert.True(Math.Abs(camera.Target.Z - bounds.Center.Z) < 1e-4f, $"Target.Z 应等于中心，实际 {camera.Target.Z}");
        Assert.True(camera.Distance > 0f);
    }
}
