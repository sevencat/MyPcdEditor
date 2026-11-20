using System.Numerics;
using MyPcdEditor.Core.Grid;
using Silk.NET.OpenGLES;

namespace MyPcdEditor.Rendering.Gles3;

/// <summary>
/// 3D 地面纹理：一个贴了栅格图的水平四边形。
/// </summary>
/// <remarks>
/// <para>
/// 只做一件事——把 <see cref="GroundTexture"/> 传成 GL 纹理，按世界范围铺一个 z 固定的大方块。
/// 数据量小（2048 上限 ≈ 8 MB），单个纹理就够，不需要分块。
/// </para>
/// <para>
/// 不生成 mipmap：全景视角下 1 texel 的墙在缩小后会被逐级平均成一片灰，整块地面看不出墙形。
/// 保持 Linear + 无 mip（墙体已在 CPU 侧加粗过），细线会轻微闪烁但形状保得住。
/// </para>
/// <para>先画地面再画点云：地面开深度写入，点云在其上正常通过深度测试。</para>
/// </remarks>
public sealed class GroundTextureRenderer : IDisposable
{
    /// <summary>每个顶点：xyz + uv，共 20 字节。</summary>
    private const int VertexStride = 5 * sizeof(float);

    private const string VertexSource = """
        #version 300 es
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec2 aUv;

        uniform mat4 uViewProj;

        out vec2 vUv;

        void main()
        {
            vUv = aUv;
            gl_Position = uViewProj * vec4(aPos, 1.0);
        }
        """;

    private const string FragmentSource = """
        #version 300 es
        precision highp float;

        in vec2 vUv;

        uniform sampler2D uMap;
        uniform float uOpacity;

        out vec4 fragColor;

        void main()
        {
            vec4 texel = texture(uMap, vUv);
            float alpha = texel.a * uOpacity;
            if (alpha < 0.02)
            {
                discard;
            }

            fragColor = vec4(texel.rgb, alpha);
        }
        """;

    private Shader? _shader;
    private uint _vao;
    private uint _vbo;
    private uint _texture;
    private int _textureWidth;
    private int _textureHeight;

    /// <summary>纹理是否已经上传（自检/诊断用）。</summary>
    public bool HasTexture => _texture != 0;

    public int TextureWidth => _textureWidth;

    public int TextureHeight => _textureHeight;

    /// <summary>在当前上下文里编译着色器、建 VAO/VBO。</summary>
    public void Initialize()
    {
        _shader = new Shader();
        _shader.CompileProgram(VertexSource, FragmentSource);

        var gl = GlContext.Gl;
        _vbo = gl.GenBuffer();
        _vao = gl.GenVertexArray();

        gl.BindVertexArray(_vao);
        gl.BindBuffer(GLEnum.ArrayBuffer, _vbo);
        unsafe
        {
            gl.VertexAttribPointer(0u, 3, GLEnum.Float, false, VertexStride, (void*)0);
            gl.VertexAttribPointer(1u, 2, GLEnum.Float, false, VertexStride, (void*)(3 * sizeof(float)));
        }

        gl.EnableVertexAttribArray(0u);
        gl.EnableVertexAttribArray(1u);
        gl.BindVertexArray(0);
        gl.BindBuffer(GLEnum.ArrayBuffer, 0);
    }

    /// <summary>
    /// 上传纹理并摆放四边形。<paramref name="z"/> 是地面在世界系里的高度。
    /// </summary>
    public void Upload(GroundTexture texture, float z)
    {
        ArgumentNullException.ThrowIfNull(texture);

        var gl = GlContext.Gl;

        if (_texture == 0)
        {
            _texture = gl.GenTexture();
        }

        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindTexture(TextureTarget.Texture2D, _texture);

        unsafe
        {
            fixed (byte* pixels = texture.Rgba)
            {
                gl.TexImage2D(
                    TextureTarget.Texture2D,
                    0,
                    InternalFormat.Rgba8,
                    (uint)texture.Width,
                    (uint)texture.Height,
                    0,
                    PixelFormat.Rgba,
                    PixelType.UnsignedByte,
                    pixels);
            }
        }

        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);

        _textureWidth = texture.Width;
        _textureHeight = texture.Height;

        // 纹理第 0 行是世界 y 最大的一行 → v=0 对应 MaxY
        var minX = (float)texture.MinX;
        var minY = (float)texture.MinY;
        var maxX = (float)texture.MaxX;
        var maxY = (float)texture.MaxY;

        float[] vertices =
        [
            minX, maxY, z, 0f, 0f,
            maxX, maxY, z, 1f, 0f,
            maxX, minY, z, 1f, 1f,

            minX, maxY, z, 0f, 0f,
            maxX, minY, z, 1f, 1f,
            minX, minY, z, 0f, 1f,
        ];

        gl.BindBuffer(GLEnum.ArrayBuffer, _vbo);
        unsafe
        {
            fixed (float* pointer = vertices)
            {
                gl.BufferData(GLEnum.ArrayBuffer, (nuint)(vertices.Length * sizeof(float)), pointer, GLEnum.StaticDraw);
            }
        }

        gl.BindBuffer(GLEnum.ArrayBuffer, 0);
        gl.BindTexture(TextureTarget.Texture2D, 0);

        GLEnum error = (GLEnum)gl.GetError();
        Console.WriteLine($"[GL] 地面纹理：{texture.Width}×{texture.Height}（块 {texture.BlockSize}）"
                          + $" 覆盖 {texture.MaxX - texture.MinX:F0}×{texture.MaxY - texture.MinY:F0} m @ z={z:F2}"
                          + $" · 墙 {texture.CountOccupied() * 100.0 / (texture.Width * texture.Height):F1}%"
                          + (error == GLEnum.NoError ? string.Empty : $" · GL 错误 {error}"));
    }

    /// <summary>画地面（必须在画点云之前调用）。</summary>
    public void Draw(in Matrix4x4 viewProjection, float opacity = 1f)
    {
        if (_shader is null || _texture == 0)
        {
            return;
        }

        var gl = GlContext.Gl;

        gl.Enable(EnableCap.DepthTest);
        gl.DepthMask(true);
        gl.Enable(EnableCap.Blend);
        gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindTexture(TextureTarget.Texture2D, _texture);

        gl.BindVertexArray(_vao);
        _shader.Use();
        _shader.SetMatrix4("uViewProj", viewProjection);
        _shader.SetFloat("uOpacity", Math.Clamp(opacity, 0f, 1f));
        _shader.SetInt("uMap", 0);

        gl.DrawArrays(PrimitiveType.Triangles, 0, 6);

        gl.BindVertexArray(0);
        gl.BindTexture(TextureTarget.Texture2D, 0);
        gl.Disable(EnableCap.Blend);
    }

    /// <summary>丢弃纹理（换图时调用）。</summary>
    public void Clear()
    {
        if (_texture != 0)
        {
            GlContext.Gl.DeleteTexture(_texture);
            _texture = 0;
        }

        _textureWidth = 0;
        _textureHeight = 0;
    }

    public void Dispose()
    {
        if (_shader is null)
        {
            return;
        }

        var gl = GlContext.Gl;

        Clear();

        if (_vbo != 0)
        {
            gl.DeleteBuffer(_vbo);
            _vbo = 0;
        }

        if (_vao != 0)
        {
            gl.DeleteVertexArray(_vao);
            _vao = 0;
        }

        _shader.Dispose();
        _shader = null;
    }
}
