using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MyPcdEditor.App.Controls;
using MyPcdEditor.App.Views;

namespace MyPcdEditor.App.Tests;

/// <summary>
/// 探明 headless 的能力边界：Skia 截图可用，GL 上下文不可用。
/// </summary>
public sealed class RenderingTests
{
    [AvaloniaFact]
    public void SkiaFrame_CanBeCaptured()
    {
        var window = new MainWindow { Width = 320, Height = 240 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        WriteableBitmap? frame = window.CaptureRenderedFrame();

        Assert.NotNull(frame);
        Assert.True(frame!.PixelSize.Width > 0);
    }

    [AvaloniaFact]
    public void GlControl_HasNoContext_UnderHeadless()
    {
        var window = new MainWindow { Width = 320, Height = 240 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var view = window.FindControl<PointCloudView>("View")!;

        // 没有 GL 上下文 → OnGlReady 从未被调用，渲染尺寸保持 0
        Assert.Null(view.Info);
        Assert.Equal(0, view.PixelWidth);
        Assert.Equal(0, view.PixelHeight);

        window.Close();
    }
}
