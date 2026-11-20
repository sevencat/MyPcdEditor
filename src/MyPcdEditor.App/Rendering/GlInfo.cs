using System.Text;
using Silk.NET.OpenGLES;

namespace MyPcdEditor.App.Rendering;

/// <summary>
/// 上下文能力快照（开机打印一次，排查驱动/回落问题时全靠它）。
/// </summary>
/// <remarks>
/// GLES3 下 <c>glGetString(GL_EXTENSIONS)</c> 是非法的，必须用 <c>GetStringi</c> 逐条取（docs/architecture.md §3）。
/// </remarks>
public sealed class GlInfo
{
    public required string Vendor { get; init; }

    public required string Renderer { get; init; }

    public required string Version { get; init; }

    public required string ShadingLanguageVersion { get; init; }

    public required int MaxTextureSize { get; init; }

    public required IReadOnlyList<string> Extensions { get; init; }

    public static GlInfo Query(GL gl)
    {
        string Safe(Func<string?> read)
        {
            try
            {
                return read() ?? "(null)";
            }
            catch (Exception ex)
            {
                return $"(不可用: {ex.GetType().Name})";
            }
        }

        var extensions = new List<string>();
        try
        {
            int count = gl.GetInteger(GetPName.NumExtensions);
            for (var i = 0; i < count; i++)
            {
                string? name = gl.GetStringS(StringName.Extensions, (uint)i);
                if (!string.IsNullOrEmpty(name))
                {
                    extensions.Add(name);
                }
            }
        }
        catch (Exception)
        {
            // 拿不到扩展表不影响渲染
        }

        int maxTextureSize = 0;
        try
        {
            maxTextureSize = gl.GetInteger(GetPName.MaxTextureSize);
        }
        catch (Exception)
        {
        }

        return new GlInfo
        {
            Vendor = Safe(() => gl.GetStringS(StringName.Vendor)),
            Renderer = Safe(() => gl.GetStringS(StringName.Renderer)),
            Version = Safe(() => gl.GetStringS(StringName.Version)),
            ShadingLanguageVersion = Safe(() => gl.GetStringS(StringName.ShadingLanguageVersion)),
            MaxTextureSize = maxTextureSize,
            Extensions = extensions,
        };
    }

    public string Describe()
    {
        var sb = new StringBuilder();
        sb.Append("Vendor=").Append(Vendor)
          .Append(" Renderer=").Append(Renderer)
          .Append(" Version=").Append(Version)
          .Append(" GLSL=").Append(ShadingLanguageVersion)
          .Append(" MaxTexture=").Append(MaxTextureSize)
          .Append(" Extensions=").Append(Extensions.Count);
        return sb.ToString();
    }
}
