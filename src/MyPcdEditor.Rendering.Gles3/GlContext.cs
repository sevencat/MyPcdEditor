using Silk.NET.OpenGLES;

namespace MyPcdEditor.Rendering.Gles3;

/// <summary>全局 GL 入口持有者（由 App 的 GL 控件在上下文就绪/释放时设置）。</summary>
/// <remarks>
/// 一个进程同时只有一个 current 上下文，因此用静态持有；渲染层的类都通过 <see cref="Gl"/> 取入口，
/// 与 C++ 侧直接调 <c>gl*</c> 的写法保持一致。
/// </remarks>
public static class GlContext
{
    private static GL? _gl;

    /// <summary>当前 GL 入口。未初始化时抛 <see cref="InvalidOperationException"/>。</summary>
    public static GL Gl => _gl ?? throw new InvalidOperationException("GL 上下文尚未初始化。");

    public static bool IsInitialized => _gl is not null;

    public static void Initialize(GL gl) => _gl = gl ?? throw new ArgumentNullException(nameof(gl));

    public static void Reset() => _gl = null;
}
