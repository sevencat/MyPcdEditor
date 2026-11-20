using System.Numerics;
using MyPcdEditor.Core.PointCloud;
using Silk.NET.OpenGLES;

namespace MyPcdEditor.Rendering.Gles3;

/// <summary>着色模式。</summary>
public enum PointColorMode
{
    /// <summary>按高度着色（turbo 色带）。</summary>
    Height = 0,

    /// <summary>单色。</summary>
    Mono = 1,
}

/// <summary>
/// 点云渲染：单个 VBO + 单个 draw call。
/// </summary>
/// <remarks>
/// <para>
/// 顶点 12 字节/点（紧密 <c>float32 xyz</c>），与 <see cref="XyzCloud"/> 的内存布局一致，
/// 因此上传可以分片直传，不需要在 CPU 侧重组（ADR-005 / ADR-006）。
/// </para>
/// <para>资源生命周期只能由渲染线程的回调驱动，不要在 UI 线程调这些方法。</para>
/// </remarks>
public sealed class PointCloudRenderer : IDisposable
{
    private const int Stride = XyzCloud.Stride;

    private const string VertexSource = """
        #version 300 es
        layout(location = 0) in vec3 aPos;

        uniform mat4 uViewProj;
        uniform float uPointSize;    // 逻辑点大小（像素 @ 1x）
        uniform float uPixelScale;   // 视口高度 / (2*tan(fov/2))
        uniform float uMinZ;
        uniform float uMaxZ;

        out float vT;

        void main()
        {
            vec4 clip = uViewProj * vec4(aPos, 1.0);
            gl_Position = clip;

            float w = max(clip.w, 1e-4);
            gl_PointSize = clamp(uPointSize * uPixelScale / w, 1.0, 32.0);

            vT = clamp((aPos.z - uMinZ) / max(uMaxZ - uMinZ, 1e-3), 0.0, 1.0);
        }
        """;

    private const string FragmentSource = """
        #version 300 es
        precision highp float;

        in float vT;
        uniform int uColorMode;

        out vec4 fragColor;

        // turbo 色带的多项式近似（Google Turbo colormap approximation）
        vec3 turbo(float x)
        {
            const vec4 kR4 = vec4(0.13572138, 4.61539260, -42.66032258, 132.13108234);
            const vec4 kG4 = vec4(0.09140261, 2.19418839, 4.84296658, -14.18503333);
            const vec4 kB4 = vec4(0.10667330, 12.64194608, -60.58204836, 110.36276771);
            const vec2 kR2 = vec2(-152.94239396, 59.28637943);
            const vec2 kG2 = vec2(4.27729857, 2.82956604);
            const vec2 kB2 = vec2(-89.90310912, 27.34824973);

            vec4 v4 = vec4(1.0, x, x * x, x * x * x);
            vec2 v2 = v4.zw * v4.z;

            return clamp(vec3(
                dot(v4, kR4) + dot(v2, kR2),
                dot(v4, kG4) + dot(v2, kG2),
                dot(v4, kB4) + dot(v2, kB2)), 0.0, 1.0);
        }

        void main()
        {
            // 圆形点：方块点在高密度点云上像噪点
            vec2 d = gl_PointCoord - vec2(0.5);
            if (dot(d, d) > 0.25)
            {
                discard;
            }

            vec3 color = uColorMode == 0 ? turbo(vT) : vec3(0.72, 0.80, 0.90);
            fragColor = vec4(color, 1.0);
        }
        """;

    private Shader? _shader;
    private uint _vao;
    private uint _vbo;
    private int _pointCount;

    /// <summary>点云总字节数（0 表示没有点云）。</summary>
    public long TotalBytes { get; private set; }

    /// <summary>已上传字节数。</summary>
    public long UploadedBytes { get; private set; }

    public int PointCount => _pointCount;

    public bool HasCloud => _pointCount > 0;

    public bool UploadComplete => HasCloud && UploadedBytes >= TotalBytes;

    /// <summary>包围盒（z 范围用于高度色带）。</summary>
    public Bounds3 Bounds { get; private set; } = new(Vector3.Zero, Vector3.One);

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
            gl.VertexAttribPointer(0u, 3, GLEnum.Float, false, Stride, (void*)0);
        }

        gl.EnableVertexAttribArray(0u);
        gl.BindVertexArray(0);
        gl.BindBuffer(GLEnum.ArrayBuffer, 0);
    }

    /// <summary>开始一次上传：分配显存。</summary>
    public void BeginUpload(int pointCount)
    {
        _pointCount = pointCount;
        TotalBytes = (long)pointCount * Stride;
        UploadedBytes = 0;

        var gl = GlContext.Gl;
        gl.BindBuffer(GLEnum.ArrayBuffer, _vbo);
        unsafe
        {
            gl.BufferData(GLEnum.ArrayBuffer, (nuint)TotalBytes, (void*)0, GLEnum.DynamicDraw);
        }

        gl.BindBuffer(GLEnum.ArrayBuffer, 0);
    }

    /// <summary>上传一片顶点数据（必须与 <see cref="BeginUpload"/> 声明的总大小一致）。</summary>
    public void UploadChunk(ReadOnlySpan<byte> chunk, long byteOffset)
    {
        if (chunk.Length == 0)
        {
            return;
        }

        var gl = GlContext.Gl;
        gl.BindBuffer(GLEnum.ArrayBuffer, _vbo);
        unsafe
        {
            fixed (byte* pointer = chunk)
            {
                gl.BufferSubData(GLEnum.ArrayBuffer, (nint)byteOffset, (nuint)chunk.Length, pointer);
            }
        }

        gl.BindBuffer(GLEnum.ArrayBuffer, 0);
        UploadedBytes += chunk.Length;
    }

    /// <summary>结束上传，记录包围盒。</summary>
    public void EndUpload(Bounds3 bounds) => Bounds = bounds;

    /// <summary>画一帧。</summary>
    public void Draw(in Matrix4x4 viewProjection, OrbitCamera camera, int viewportHeight, PointColorMode mode, float pointSize)
    {
        if (_shader is null || !HasCloud)
        {
            return;
        }

        var gl = GlContext.Gl;

        gl.Enable(EnableCap.DepthTest);
        gl.BindVertexArray(_vao);
        _shader.Use();

        _shader.SetMatrix4("uViewProj", viewProjection);
        _shader.SetFloat("uPointSize", MathF.Max(pointSize, 0.5f));
        _shader.SetFloat("uPixelScale", camera.PixelScale(viewportHeight));
        _shader.SetFloat("uMinZ", Bounds.Min.Z);
        _shader.SetFloat("uMaxZ", Bounds.Max.Z);
        _shader.SetInt("uColorMode", (int)mode);

        gl.DrawArrays(PrimitiveType.Points, 0, (uint)_pointCount);

        gl.BindVertexArray(0);
        gl.Disable(EnableCap.DepthTest);
    }

    /// <summary>丢弃当前点云（换图时调用）。</summary>
    public void Clear()
    {
        _pointCount = 0;
        TotalBytes = 0;
        UploadedBytes = 0;
    }

    public void Dispose()
    {
        if (_shader is null)
        {
            return;
        }

        var gl = GlContext.Gl;

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
        _pointCount = 0;
    }
}
