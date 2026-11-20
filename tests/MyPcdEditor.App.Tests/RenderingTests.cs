using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MyPcdEditor.App.Controls;
using MyPcdEditor.App.Views;
using MyPcdEditor.Rendering.Gles3;

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

    /// <summary>抽样点数按步长向上取整：不能少覆盖最后一个点。</summary>
    [Fact]
    public void SamplePointCount_CoversWholeCloud()
    {
        Assert.Equal(1000, PointCloudRenderer.SamplePointCount(1000, 1));
        Assert.Equal(500, PointCloudRenderer.SamplePointCount(1000, 2));
        Assert.Equal(250, PointCloudRenderer.SamplePointCount(1000, 4));

        // 不能整除时向上取整
        Assert.Equal(3, PointCloudRenderer.SamplePointCount(9, 4));

        // 步长非法按全量处理
        Assert.Equal(10, PointCloudRenderer.SamplePointCount(10, 0));
        Assert.Equal(10, PointCloudRenderer.SamplePointCount(10, -3));
    }

    /// <summary>分块在抽样索引缓冲里的区间：块与块拼接后要正好覆盖全部索引。</summary>
    [Fact]
    public void SampleIndexRange_PartitionsIndexBuffer()
    {
        // 步长 4、共 10 点 → 索引 0,4,8
        Assert.Equal((0, 3), PointCloudRenderer.SampleIndexRange(0, 10, 4));

        // 前 4 个点 → 索引 0（索引 4 属于下一块）
        Assert.Equal((0, 1), PointCloudRenderer.SampleIndexRange(0, 4, 4));

        // [4,8) → 索引 4
        Assert.Equal((1, 1), PointCloudRenderer.SampleIndexRange(4, 4, 4));

        // [8,10) → 索引 8
        Assert.Equal((2, 1), PointCloudRenderer.SampleIndexRange(8, 2, 4));

        // 区间内没有落在 step 倍数上的点
        Assert.Equal((1, 0), PointCloudRenderer.SampleIndexRange(1, 2, 4));

        // 不抽样时就是顶点区间
        Assert.Equal((0, 512), PointCloudRenderer.SampleIndexRange(1024, 512, 1));

        // 块区间必须连续且首尾相接，否则剔除时会漏画
        int covered = 0;
        for (int first = 0; first < 1024; first += 128)
        {
            (int start, int count) = PointCloudRenderer.SampleIndexRange(first, 128, 3);
            Assert.Equal(covered, start);
            covered += count;
        }

        Assert.Equal(PointCloudRenderer.SamplePointCount(1024, 3), covered);
    }

    /// <summary>工具栏「细节」下拉必须真的改到视图的抽样步长。</summary>
    [AvaloniaFact]
    public void DetailBox_SetsDrawStep()
    {
        var window = new MainWindow { Width = 320, Height = 240 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var view = window.FindControl<PointCloudView>("View")!;
        var box = window.FindControl<ComboBox>("DetailBox")!;

        view.DrawStep = 1;
        box.SelectedIndex = 2;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(4, view.DrawStep);

        box.SelectedIndex = 0;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, view.DrawStep);

        window.Close();
    }
}
