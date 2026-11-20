using Avalonia;

namespace MyPcdEditor.App;

internal static class Program
{
    // AppMain 之前不要碰 Avalonia/第三方 API：SynchronizationContext 还没配好。
    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
        catch (IOException)
        {
            // GUI 子系统没有控制台时忽略
        }

        // 无界面自检：只走 IO 层，便于在 CI/终端里验证数据能否读出（不建 GL 上下文）
        if (args.Length >= 2 && args[0] is "--load" or "-l")
        {
            RunHeadlessLoad(args[1]);
            return;
        }

        // 渲染自检：真窗口渲染一帧后读回像素，按统计结果给退出码（0 通过 / 1 失败）
        if (args.Length >= 2 && args[0] is "--selftest" or "-t")
        {
            StartupOptions.SetSelfTestDirectory(args[1]);
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>打印地图包与点云摘要，用于冒烟验证。</summary>
    private static void RunHeadlessLoad(string directory)
    {
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Core.MapPackage package = Core.MapPackage.Open(directory);
            Console.WriteLine($"包目录: {package.Directory}");
            Console.WriteLine($"  pcd: {package.PcdPath ?? "(无)"}");
            Console.WriteLine($"  pgm: {package.PgmPath ?? "(无)"}");
            Console.WriteLine($"  yaml: {package.YamlPath ?? "(无)"}");

            if (package.Yaml is { } yaml)
            {
                Console.WriteLine($"  resolution={yaml.Resolution} origin=({yaml.OriginX}, {yaml.OriginY}) negate={yaml.Negate}");
            }

            if (package.ProjectedOrigin is { } origin)
            {
                Console.WriteLine($"  projected_origin=({origin.X}, {origin.Y}, {origin.Z}) zone={origin.Zone}");
            }

            if (package.PgmPath is not null)
            {
                Core.Grid.PgmImage pgm = Core.Grid.PgmReader.Read(package.PgmPath);
                Console.WriteLine($"  栅格: {pgm.Width}×{pgm.Height} maxval={pgm.MaxValue}");
            }

            if (package.PcdPath is not null)
            {
                using Core.PointCloud.XyzCloud cloud = Core.PointCloud.XyzCloud.Open(package.PcdPath);
                watch.Stop();
                Console.WriteLine($"  点云: {cloud.PointCount:N0} 点, 零拷贝={cloud.IsZeroCopy}, 耗时={watch.ElapsedMilliseconds} ms");
                Console.WriteLine($"  包围盒: min=({cloud.Bounds.Min.X:F2}, {cloud.Bounds.Min.Y:F2}, {cloud.Bounds.Min.Z:F2}) " +
                                  $"max=({cloud.Bounds.Max.X:F2}, {cloud.Bounds.Max.Y:F2}, {cloud.Bounds.Max.Z:F2})");
                Console.WriteLine($"  字节数: {cloud.ByteLength:N0}");
            }

            Console.WriteLine("OK");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL: {ex.GetType().Name}: {ex.Message}");
            Environment.ExitCode = 1;
        }
    }

    /// <summary>设计器与单元测试共用的配置入口。</summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // 走 ANGLE（EGL → D3D11）：GLES 3.x 是 M0 的前提，起不来时 Avalonia 会回落
            .With(new Win32PlatformOptions
            {
                RenderingMode = [Win32RenderingMode.AngleEgl],
            })
            .WithInterFont()
            .LogToTrace();
}
