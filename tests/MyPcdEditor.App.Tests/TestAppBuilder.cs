using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(MyPcdEditor.App.Tests.TestAppBuilder))]

namespace MyPcdEditor.App.Tests;

/// <summary>
/// headless 测试用的 Avalonia 宿主：无窗口系统、无 GL 上下文，但保留 Skia 软件渲染，
/// 这样 2D 控件可以做截图断言（GL 控件在 headless 下不渲染，见 docs/testing.md）。
/// </summary>
internal static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder
        .Configure<MyPcdEditor.App.App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions
        {
            UseHeadlessDrawing = false,
        });
}
