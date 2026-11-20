using System.Numerics;
using MyPcdEditor.Core.Geometry;
using MyPcdEditor.Core.PointCloud;

namespace MyPcdEditor.Core.Tests;

/// <summary>视锥剔除与分块包围盒的纯逻辑测试（不碰 GL）。</summary>
public sealed class CullingTests
{
    // ---- 视锥 ----

    /// <summary>单位矩阵：视锥就是 NDC 立方体 [-1,1]³。</summary>
    [Fact]
    public void Frustum_IdentityMatrix_IsUnitCube()
    {
        Frustum frustum = Frustum.FromViewProjection(Matrix4x4.Identity);

        Assert.True(frustum.Contains(Vector3.Zero));
        Assert.True(frustum.Contains(new Vector3(0.9f, -0.9f, 0.9f)));
        Assert.False(frustum.Contains(new Vector3(1.5f, 0f, 0f)));
        Assert.False(frustum.Contains(new Vector3(0f, 0f, -1.5f)));
    }

    /// <summary>包围盒完全在外面才剔除；压边的盒子必须保留。</summary>
    [Fact]
    public void Frustum_AabbCull_KeepsStraddlingBoxes()
    {
        Frustum frustum = Frustum.FromViewProjection(Matrix4x4.Identity);

        // 完全在里面
        Assert.True(frustum.Intersects(Box(-0.5f, 0.5f)));

        // 完全在右侧外面
        Assert.False(frustum.Intersects(Box(1.2f, 2f)));

        // 压着右边界：仍然可见（保守，不能漏画）
        Assert.True(frustum.Intersects(Box(0.8f, 1.4f)));
    }

    /// <summary>透视相机：相机朝 -Z 看，身后和侧后方的物体要被剔除。</summary>
    [Fact]
    public void Frustum_Perspective_CullsBehindCamera()
    {
        Matrix4x4 view = Matrix4x4.CreateLookAt(new Vector3(0, 0, 10), Vector3.Zero, Vector3.UnitY);
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI / 3f, 16f / 9f, 0.1f, 100f);
        Frustum frustum = Frustum.FromViewProjection(view * projection);

        // 原点在正前方
        Assert.True(frustum.Contains(Vector3.Zero));

        // 相机身后（z 更大）看不见
        Assert.False(frustum.Contains(new Vector3(0, 0, 20)));

        // 太远（超出远平面）
        Assert.False(frustum.Contains(new Vector3(0, 0, -200)));

        // 侧向远处：超出水平视角
        Assert.False(frustum.Contains(new Vector3(500, 0, 0)));
    }

    /// <summary>退化矩阵不能误杀——剔除失效可以接受，漏画不行。</summary>
    [Fact]
    public void Frustum_DegenerateMatrix_KeepsEverything()
    {
        Frustum frustum = Frustum.FromViewProjection(default);

        Assert.True(frustum.Intersects(Box(-1e6f, 1e6f)));
        Assert.True(frustum.Contains(new Vector3(1e6f, 1e6f, 1e6f)));
    }

    // ---- 分块包围盒 ----

    /// <summary>点按索引切块，每块的包围盒只看自己的点。</summary>
    [Fact]
    public void ChunkBounds_SplitsByPointIndex()
    {
        // 4 个点，每块 2 个：(0,0,0) (1,0,0) | (10,0,0) (11,0,0)
        var builder = new ChunkBoundsBuilder(4, chunkPoints: 2);
        builder.Add(Pack(
            new Vector3(0, 0, 0),
            new Vector3(1, 0, 0),
            new Vector3(10, 0, 0),
            new Vector3(11, 0, 0)), 0);

        ChunkRange[] chunks = builder.Build(new Bounds3(Vector3.Zero, Vector3.One));

        Assert.Equal(2, chunks.Length);
        Assert.Equal(0, chunks[0].FirstPoint);
        Assert.Equal(2, chunks[0].PointCount);
        Assert.Equal(0f, chunks[0].Bounds.Min.X);
        Assert.Equal(1f, chunks[0].Bounds.Max.X);

        Assert.Equal(2, chunks[1].FirstPoint);
        Assert.Equal(10f, chunks[1].Bounds.Min.X);
        Assert.Equal(11f, chunks[1].Bounds.Max.X);
    }

    /// <summary>最后一块不满时点数正确（不能多算，否则越界画）。</summary>
    [Fact]
    public void ChunkBounds_LastChunkIsShort()
    {
        var builder = new ChunkBoundsBuilder(5, chunkPoints: 2);
        builder.Add(Pack(Enumerable.Range(0, 5).Select(i => new Vector3(i, 0, 0)).ToArray()), 0);

        ChunkRange[] chunks = builder.Build(new Bounds3(Vector3.Zero, Vector3.One));

        Assert.Equal(3, chunks.Length);
        Assert.Equal(1, chunks[2].PointCount);
        Assert.Equal(5, chunks[2].EndPoint);
    }

    /// <summary>分片上传时偏移要对上：第二片从第 2 个点开始。</summary>
    [Fact]
    public void ChunkBounds_HandlesSlicedUpload()
    {
        var builder = new ChunkBoundsBuilder(4, chunkPoints: 2);
        builder.Add(Pack(new Vector3(0, 0, 0), new Vector3(1, 0, 0)), 0);
        builder.Add(Pack(new Vector3(10, 0, 0), new Vector3(11, 0, 0)), 2 * XyzCloud.Stride);

        ChunkRange[] chunks = builder.Build(new Bounds3(Vector3.Zero, Vector3.One));

        Assert.Equal(10f, chunks[1].Bounds.Min.X);
    }

    /// <summary>没收到任何点的块用兜底包围盒，保证不会被剔除（保守）。</summary>
    [Fact]
    public void ChunkBounds_UntouchedChunkUsesFallback()
    {
        var builder = new ChunkBoundsBuilder(4, chunkPoints: 2);
        builder.Add(Pack(new Vector3(0, 0, 0), new Vector3(1, 0, 0)), 0);

        var fallback = new Bounds3(new Vector3(-9, -9, -9), new Vector3(9, 9, 9));
        ChunkRange[] chunks = builder.Build(fallback);

        Assert.Equal(fallback, chunks[1].Bounds);
        Assert.Equal(2, chunks[1].PointCount);
    }

    /// <summary>非有限值不参与包围盒，否则一个 NaN 就能把盒子撑到无穷大。</summary>
    [Fact]
    public void ChunkBounds_SkipsNonFinite()
    {
        var builder = new ChunkBoundsBuilder(2, chunkPoints: 2);
        builder.Add(Pack(new Vector3(0, 0, 0), new Vector3(float.NaN, 0, 0)), 0);

        ChunkRange[] chunks = builder.Build(new Bounds3(Vector3.Zero, Vector3.One));

        Assert.True(float.IsFinite(chunks[0].Bounds.Max.X));
        Assert.Equal(0f, chunks[0].Bounds.Max.X);
    }

    /// <summary>未按点对齐的分片必须报错，而不是悄悄错位。</summary>
    [Fact]
    public void ChunkBounds_RejectsUnalignedInput()
    {
        var builder = new ChunkBoundsBuilder(4, chunkPoints: 2);

        Assert.Throws<ArgumentOutOfRangeException>(() => builder.Add(new byte[XyzCloud.Stride], 1));
        Assert.Throws<ArgumentException>(() => builder.Add(new byte[XyzCloud.Stride - 1], 0));
    }

    private static Bounds3 Box(float min, float max) =>
        new(new Vector3(min, min, min), new Vector3(max, max, max));

    private static byte[] Pack(params Vector3[] points)
    {
        var bytes = new byte[points.Length * XyzCloud.Stride];

        for (int i = 0; i < points.Length; i++)
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(i * XyzCloud.Stride), points[i].X);
            BitConverter.TryWriteBytes(bytes.AsSpan((i * XyzCloud.Stride) + 4), points[i].Y);
            BitConverter.TryWriteBytes(bytes.AsSpan((i * XyzCloud.Stride) + 8), points[i].Z);
        }

        return bytes;
    }
}
