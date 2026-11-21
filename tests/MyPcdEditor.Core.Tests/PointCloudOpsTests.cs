using System.Numerics;
using MyPcdEditor.Core;
using MyPcdEditor.Core.PointCloud;

namespace MyPcdEditor.Core.Tests;

/// <summary>点云算子（体素降采样 / SOR / 裁剪）的行为。</summary>
public sealed class PointCloudOpsTests
{
    private static XyzCloud MakeCloud(params Vector3[] points)
    {
        var data = new byte[points.Length * XyzCloud.Stride];
        for (var i = 0; i < points.Length; i++)
        {
            BitConverter.TryWriteBytes(data.AsSpan(i * XyzCloud.Stride, 4), points[i].X);
            BitConverter.TryWriteBytes(data.AsSpan((i * XyzCloud.Stride) + 4, 4), points[i].Y);
            BitConverter.TryWriteBytes(data.AsSpan((i * XyzCloud.Stride) + 8, 4), points[i].Z);
        }

        return XyzCloud.Create(data, points.Length);
    }

    private static Vector3 PointAt(XyzCloud cloud, int index)
    {
        ReadOnlySpan<byte> bytes = cloud.GetBytes(index * XyzCloud.Stride, XyzCloud.Stride);
        return new Vector3(
            BitConverter.ToSingle(bytes[..4]),
            BitConverter.ToSingle(bytes.Slice(4, 4)),
            BitConverter.ToSingle(bytes.Slice(8, 4)));
    }

    [Fact]
    public void VoxelDownsample_MergesPointsInSameVoxel()
    {
        XyzCloud cloud = MakeCloud(
            new Vector3(0.00f, 0.00f, 0.00f),
            new Vector3(0.02f, 0.02f, 0.02f),
            new Vector3(0.04f, 0.00f, 0.00f));

        XyzCloud result = PointCloudOps.VoxelDownsample(cloud, 0.1f);

        Assert.Equal(1, result.PointCount);
        Vector3 centroid = PointAt(result, 0);
        Assert.Equal(0.02f, centroid.X, 3);
        Assert.Equal(0.0067f, centroid.Y, 3);
        Assert.Equal(0.0067f, centroid.Z, 3);
    }

    [Fact]
    public void VoxelDownsample_SplitsAcrossVoxelBoundary()
    {
        XyzCloud cloud = MakeCloud(
            new Vector3(0.05f, 0.05f, 0.05f),
            new Vector3(0.15f, 0.05f, 0.05f));

        XyzCloud result = PointCloudOps.VoxelDownsample(cloud, 0.1f);

        Assert.Equal(2, result.PointCount);
    }

    [Fact]
    public void VoxelDownsample_SkipsNonFinitePoints()
    {
        XyzCloud cloud = MakeCloud(
            new Vector3(float.NaN, 0, 0),
            new Vector3(0, float.PositiveInfinity, 0),
            new Vector3(1, 1, 1));

        XyzCloud result = PointCloudOps.VoxelDownsample(cloud, 0.1f);

        Assert.Equal(1, result.PointCount);
        Assert.Equal(new Vector3(1, 1, 1), PointAt(result, 0));
    }

    [Fact]
    public void VoxelDownsample_AssignsVoxelsRelativeToBounds()
    {
        // 体素原点取包围盒最小角，所以正好压在两格交界上的两个点属于不同格
        XyzCloud cloud = MakeCloud(
            new Vector3(10.0f, 0, 0),
            new Vector3(10.099f, 0, 0),
            new Vector3(10.101f, 0, 0));

        XyzCloud result = PointCloudOps.VoxelDownsample(cloud, 0.1f);

        Assert.Equal(2, result.PointCount);
    }

    [Fact]
    public void VoxelDownsample_EmptyCloudStaysEmpty()
    {
        XyzCloud result = PointCloudOps.VoxelDownsample(MakeCloud(), 0.1f);

        Assert.Equal(0, result.PointCount);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-0.1f)]
    [InlineData(float.NaN)]
    public void VoxelDownsample_RejectsInvalidVoxelSize(float size)
    {
        XyzCloud cloud = MakeCloud(new Vector3(0, 0, 0));

        Assert.Throws<ArgumentOutOfRangeException>(() => PointCloudOps.VoxelDownsample(cloud, size));
    }

    [Fact]
    public void CropBox_KeepsOnlyInside()
    {
        XyzCloud cloud = MakeCloud(
            new Vector3(0, 0, 0),
            new Vector3(5, 5, 5),
            new Vector3(20, 0, 0),
            new Vector3(-3, 0, 0));

        var box = new Bounds3(new Vector3(-1, -1, -1), new Vector3(10, 10, 10));
        XyzCloud result = PointCloudOps.CropBox(cloud, box);

        Assert.Equal(2, result.PointCount);
        Assert.Equal(new Vector3(0, 0, 0), PointAt(result, 0));
        Assert.Equal(new Vector3(5, 5, 5), PointAt(result, 1));
    }

    [Fact]
    public void CropBox_CanInvertToExclude()
    {
        XyzCloud cloud = MakeCloud(
            new Vector3(0, 0, 0),
            new Vector3(20, 0, 0));

        var box = new Bounds3(new Vector3(-1, -1, -1), new Vector3(10, 10, 10));
        XyzCloud result = PointCloudOps.CropBox(cloud, box, keepInside: false);

        Assert.Equal(1, result.PointCount);
        Assert.Equal(new Vector3(20, 0, 0), PointAt(result, 0));
    }

    [Fact]
    public void CropBox_IncludesPointsOnBoundary()
    {
        XyzCloud cloud = MakeCloud(new Vector3(10, 10, 10));
        var box = new Bounds3(Vector3.Zero, new Vector3(10, 10, 10));

        Assert.Equal(1, PointCloudOps.CropBox(cloud, box).PointCount);
    }

    [Fact]
    public void OutlierRemoval_DropsIsolatedPoint()
    {
        // 3×3×3 的密集格点（间距 0.02 m）+ 一个 5 m 外的孤立点
        var points = new List<Vector3>();
        for (var x = 0; x < 3; x++)
        {
            for (var y = 0; y < 3; y++)
            {
                for (var z = 0; z < 3; z++)
                {
                    points.Add(new Vector3(x * 0.02f, y * 0.02f, z * 0.02f));
                }
            }
        }

        points.Add(new Vector3(5f, 5f, 5f));

        XyzCloud cloud = MakeCloud([.. points]);

        // 簇内近邻距离几乎一致，stdRatio=2 留足余量；孤点 5×5×5 邻域内空无一物，必删
        XyzCloud result = PointCloudOps.RemoveStatisticalOutliers(cloud, neighborCount: 6, stdRatio: 2.0f);

        Assert.Equal(27, result.PointCount);
    }

    [Fact]
    public void OutlierRemoval_LoneFarPointIsDroppedWithNoNeighbors()
    {
        // 两个靠得住的点 + 一个远到邻域里没人：远点必须删（不能用"距离 0"豁免它）
        XyzCloud cloud = MakeCloud(
            new Vector3(100f, 100f, 100f),
            new Vector3(0, 0, 0),
            new Vector3(0.01f, 0, 0),
            new Vector3(0, 0.01f, 0));

        XyzCloud result = PointCloudOps.RemoveStatisticalOutliers(cloud, neighborCount: 2, stdRatio: 5f);

        Assert.Equal(3, result.PointCount);
    }

    [Fact]
    public void OutlierRemoval_KeepsUniformCloudIntactWithLooseThreshold()
    {
        var points = new List<Vector3>();
        for (var x = 0; x < 4; x++)
        {
            for (var y = 0; y < 4; y++)
            {
                points.Add(new Vector3(x * 0.1f, y * 0.1f, 0));
            }
        }

        XyzCloud cloud = MakeCloud([.. points]);

        // stdRatio 放大到 10：只有极端离群点才会被删，均匀云应一个不少
        Assert.Equal(16, PointCloudOps.RemoveStatisticalOutliers(cloud, 4, 10f).PointCount);
    }

    [Fact]
    public void OutlierRemoval_DropsNonFinitePointsRegardless()
    {
        XyzCloud cloud = MakeCloud(
            new Vector3(0, 0, 0),
            new Vector3(0.01f, 0, 0),
            new Vector3(float.NaN, 0, 0));

        XyzCloud result = PointCloudOps.RemoveStatisticalOutliers(cloud, 2, 5f);

        Assert.Equal(2, result.PointCount);
    }

    [Fact]
    public void OutlierRemoval_RejectsBadParameters()
    {
        XyzCloud cloud = MakeCloud(new Vector3(0, 0, 0), new Vector3(1, 0, 0));

        Assert.Throws<ArgumentOutOfRangeException>(() => PointCloudOps.RemoveStatisticalOutliers(cloud, 1, 1f));
        Assert.Throws<ArgumentOutOfRangeException>(() => PointCloudOps.RemoveStatisticalOutliers(cloud, 8, -1f));
    }

    // ── 框选（屏幕矩形）──────────────────────────────────────────────────────

    /// <summary>投影矩阵取单位阵时世界坐标就是 NDC：x&lt;0 落在屏幕左半、x&gt;0 在右半。</summary>
    [Fact]
    public void CropScreenBox_RemovesPointsInsideBox()
    {
        XyzCloud cloud = MakeCloud(
            new Vector3(-0.5f, 0f, 0f),
            new Vector3(-0.2f, 0.1f, 0f),
            new Vector3(0.3f, 0f, 0f),
            new Vector3(0.8f, -0.2f, 0f));

        // 框住屏幕右半：u ∈ [0.5, 1] → x ∈ [0, 1]
        XyzCloud result = PointCloudOps.CropScreenBox(
            cloud, Matrix4x4.Identity, new ScreenBox(0.5f, 0f, 1f, 1f));

        Assert.Equal(2, result.PointCount);
        Assert.Equal(-0.5f, PointAt(result, 0).X, 4);
        Assert.Equal(-0.2f, PointAt(result, 1).X, 4);
    }

    [Fact]
    public void CropScreenBox_KeepInside_KeepsOnlyBox()
    {
        XyzCloud cloud = MakeCloud(
            new Vector3(-0.5f, 0f, 0f),
            new Vector3(0.3f, 0f, 0f));

        XyzCloud result = PointCloudOps.CropScreenBox(
            cloud, Matrix4x4.Identity, new ScreenBox(0.5f, 0f, 1f, 1f), keepInside: true);

        Assert.Equal(1, result.PointCount);
        Assert.Equal(0.3f, PointAt(result, 0).X, 4);
    }

    /// <summary>相机后方的点（w ≤ 0）投影到屏幕上是个镜像位置，不能算框内。</summary>
    [Fact]
    public void CropScreenBox_IgnoresPointsBehindCamera()
    {
        Matrix4x4 flipped = Matrix4x4.Identity;
        flipped.M44 = -1f;

        XyzCloud cloud = MakeCloud(new Vector3(0, 0, 0), new Vector3(0.5f, 0.5f, 0));

        XyzCloud result = PointCloudOps.CropScreenBox(
            cloud, flipped, new ScreenBox(0f, 0f, 1f, 1f), keepInside: true);

        Assert.Equal(0, result.PointCount);
    }

    [Fact]
    public void CropScreenBox_DropsNonFinitePoints()
    {
        XyzCloud cloud = MakeCloud(new Vector3(float.NaN, 0, 0), new Vector3(-0.5f, 0, 0));

        XyzCloud result = PointCloudOps.CropScreenBox(
            cloud, Matrix4x4.Identity, new ScreenBox(0.5f, 0f, 1f, 1f));

        Assert.Equal(1, result.PointCount);
        Assert.Equal(-0.5f, PointAt(result, 0).X, 4);
    }

    [Fact]
    public void CropScreenBox_RejectsEmptyBox()
    {
        XyzCloud cloud = MakeCloud(new Vector3(0, 0, 0));

        Assert.Throws<ArgumentException>(() => PointCloudOps.CropScreenBox(
            cloud, Matrix4x4.Identity, new ScreenBox(0.5f, 0.5f, 0.5f, 0.5f)));
    }

    /// <summary>
    /// 体素表按 <c>hash &amp; (capacity-1)</c> 取槽，容量会随点数涨到 2^26。
    /// 散列若只留 24 位，高位槽永远空着、低位槽挤爆，32 M 点降采样会从几秒退化到几分钟没完
    /// （2026-11-21 实测踩过）。这里直接盯住值域。
    /// </summary>
    [Fact]
    public void VoxelHash_SpreadsAcrossFullIntRange()
    {
        var random = new Random(12345);
        const int samples = 200_000;
        var wide = 0;

        for (var i = 0; i < samples; i++)
        {
            long key = VoxelHash.Pack(
                random.Next(-1_000_000, 1_000_001),
                random.Next(-1_000_000, 1_000_001),
                random.Next(-1000, 1001));

            if ((uint)VoxelHash.Hash(key) >= 1u << 24)
            {
                wide++;
            }
        }

        // 均匀的 32 位散列里，落在 [2^24, 2^32) 的比例应接近 99.2%
        Assert.True(wide > samples * 0.98, $"散列值域太窄：{wide}/{samples} 落在高 8 位区间");
    }
}
