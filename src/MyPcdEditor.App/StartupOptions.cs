namespace MyPcdEditor.App;

/// <summary>
/// 本程序自有的启动参数（在 Avalonia 初始化之前解析，见 <see cref="Program"/>）。
/// </summary>
internal static class StartupOptions
{
    /// <summary>
    /// <c>--selftest &lt;目录&gt;</c>：真窗口渲染一帧后读回像素，据像素统计给出 PASS/FAIL 与退出码。
    /// </summary>
    public static string? SelfTestDirectory { get; private set; }

    public static bool SelfTest => SelfTestDirectory is not null;

    public static void SetSelfTestDirectory(string directory)
        => SelfTestDirectory = Path.GetFullPath(directory);
}
