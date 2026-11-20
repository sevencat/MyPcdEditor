using System.Numerics;
using MyPcdEditor.Core.Geometry;
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
/// 点云渲染：单个 VBO，按连续点区间分块绘制，逐块做视锥剔除。
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
    private uint _ibo;
    private int _pointCount;
    private int _indexStep;
    private int _indexCount;

    // 分块剔除：点区间 + 包围盒，以及每块在（抽样后的）索引缓冲里的连续区间
    private ChunkRange[] _chunks = [];
    private int[] _chunkIndexFirst = [];
    private int[] _chunkIndexCount = [];

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

        // 点数变了，抽样索引与分块信息都作废（分块由上传结束后 SetChunks 重新给）
        SetChunks([]);

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

    /// <summary>
    /// 抽样步长：1 表示全量绘制，N 表示每隔 N 个点画一个（索引抽样，覆盖全点云）。
    /// </summary>
    public int SampleStep { get; set; } = 1;

    /// <summary>
    /// 是否开启视锥剔除。<c>PGV_NO_CULL=1</c> 关闭（A/B 与基准测试用）。
    /// </summary>
    public bool CullingEnabled { get; set; } =
        Environment.GetEnvironmentVariable("PGV_NO_CULL") != "1";

    /// <summary>总块数（0 表示还没拿到分块信息，此时整块画）。</summary>
    public int TotalChunkCount => _chunks.Length;

    /// <summary>上一帧通过视锥测试的块数。</summary>
    public int VisibleChunkCount { get; private set; }

    /// <summary>上一帧实际提交的点数（抽样 + 剔除之后）。</summary>
    public int DrawnPointCount { get; private set; }

    /// <summary>按步长抽样后的总点数（纯计算，UI 与测试共用）。</summary>
    public static int SamplePointCount(int pointCount, int step) =>
        step <= 1 ? pointCount : (pointCount + step - 1) / step;

    /// <summary>
    /// 一块点在抽样索引缓冲里对应的连续区间。
    /// </summary>
    /// <remarks>
    /// 索引按点序升序生成（第 k 个索引 = k*step），所以任意连续点区间都对应索引缓冲里的一段连续区间，
    /// 剔除时就能"一块一次 DrawElements"，不需要给每块单独建索引。
    /// </remarks>
    public static (int First, int Count) SampleIndexRange(int firstPoint, int pointCount, int step)
    {
        if (step <= 1 || pointCount <= 0)
        {
            return (0, Math.Max(pointCount, 0));
        }

        int start = (firstPoint + step - 1) / step;
        int end = (firstPoint + pointCount + step - 1) / step;
        return (start, Math.Max(end - start, 0));
    }

    /// <summary>设置分块表（上传完成后调用）。</summary>
    public void SetChunks(ChunkRange[] chunks)
    {
        _chunks = chunks ?? [];
        _chunkIndexFirst = new int[_chunks.Length];
        _chunkIndexCount = new int[_chunks.Length];

        // 索引区间要按新的块重算
        _indexStep = 0;
    }

    /// <summary>
    /// 按步长重建索引缓冲；步长没变就什么都不做。
    /// </summary>
    /// <remarks>
    /// 索引抽样而不是"只画前 1/N 个点"：点云按扫描顺序排列，前缀截断会让视野里只剩一小块区域。
    /// 索引本身是 32 M/step 个 uint（1/4 抽样约 32 MB），相对点云 VBO 是零头。
    /// </remarks>
    private void EnsureIndices(int step)
    {
        if (step <= 1)
        {
            _indexStep = 1;
            _indexCount = 0;
            return;
        }

        if (_indexStep == step && _indexCount > 0)
        {
            return;
        }

        int count = (_pointCount + step - 1) / step;
        var indices = new uint[count];
        for (int i = 0; i < count; i++)
        {
            indices[i] = (uint)(i * step);
        }

        var gl = GlContext.Gl;
        if (_ibo == 0)
        {
            _ibo = gl.GenBuffer();
        }

        gl.BindBuffer(GLEnum.ElementArrayBuffer, _ibo);
        unsafe
        {
            fixed (uint* pointer = indices)
            {
                gl.BufferData(GLEnum.ElementArrayBuffer, (nuint)(count * sizeof(uint)), pointer, GLEnum.DynamicDraw);
            }
        }

        gl.BindBuffer(GLEnum.ElementArrayBuffer, 0);

        _indexStep = step;
        _indexCount = count;

        // 每块在索引缓冲里的连续区间
        for (int c = 0; c < _chunks.Length; c++)
        {
            (int first, int length) = SampleIndexRange(_chunks[c].FirstPoint, _chunks[c].PointCount, step);
            _chunkIndexFirst[c] = first;
            _chunkIndexCount[c] = length;
        }
    }

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

        int step = Math.Max(SampleStep, 1);
        if (step > 1)
        {
            EnsureIndices(step);

            // 索引缓冲挂在 VAO 上，VAO 已经 Bind，这里只需绑一下 IBO
            gl.BindBuffer(GLEnum.ElementArrayBuffer, _ibo);
        }

        DrawChunks(viewProjection, step, CullingEnabled && _chunks.Length > 0);

        gl.BindVertexArray(0);
        gl.Disable(EnableCap.DepthTest);
    }

    /// <summary>逐块提交，跳过完全落在视锥外的块。</summary>
    private void DrawChunks(in Matrix4x4 viewProjection, int step, bool cull)
    {
        if (_chunks.Length == 0)
        {
            // 还没有分块信息（上传中的帧）：整块画
            VisibleChunkCount = 0;
            DrawnPointCount = SamplePointCount(_pointCount, step);
            SubmitRange(step, 0, step <= 1 ? _pointCount : _indexCount);
            return;
        }

        Frustum frustum = cull ? Frustum.FromViewProjection(viewProjection) : default;

        int visible = 0;
        int drawn = 0;

        for (int c = 0; c < _chunks.Length; c++)
        {
            ChunkRange chunk = _chunks[c];
            if (chunk.IsEmpty || (cull && !frustum.Intersects(chunk.Bounds)))
            {
                continue;
            }

            if (step <= 1)
            {
                SubmitRange(step, chunk.FirstPoint, chunk.PointCount);
                drawn += chunk.PointCount;
                visible++;
            }
            else
            {
                int indexCount = _chunkIndexCount[c];
                if (indexCount <= 0)
                {
                    continue;
                }

                SubmitRange(step, _chunkIndexFirst[c], indexCount);
                drawn += indexCount;
                visible++;
            }
        }

        VisibleChunkCount = visible;
        DrawnPointCount = drawn;
    }

    /// <summary>提交一段点：<paramref name="step"/> 为 1 时是顶点区间，否则是索引缓冲里的索引区间。</summary>
    private static void SubmitRange(int step, int first, int count)
    {
        if (count <= 0)
        {
            return;
        }

        var gl = GlContext.Gl;

        if (step <= 1)
        {
            gl.DrawArrays(PrimitiveType.Points, first, (uint)count);
            return;
        }

        unsafe
        {
            gl.DrawElements(PrimitiveType.Points, (uint)count, GLEnum.UnsignedInt, (void*)((nint)first * sizeof(uint)));
        }
    }

    /// <summary>丢弃当前点云（换图时调用）。</summary>
    public void Clear()
    {
        _pointCount = 0;
        TotalBytes = 0;
        UploadedBytes = 0;
        DrawnPointCount = 0;
        VisibleChunkCount = 0;
        SetChunks([]);
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

        if (_ibo != 0)
        {
            gl.DeleteBuffer(_ibo);
            _ibo = 0;
        }

        if (_vao != 0)
        {
            gl.DeleteVertexArray(_vao);
            _vao = 0;
        }

        _shader.Dispose();
        _shader = null;
        _pointCount = 0;
        _indexStep = 0;
        _indexCount = 0;
        _chunks = [];
        _chunkIndexFirst = [];
        _chunkIndexCount = [];
    }
}
