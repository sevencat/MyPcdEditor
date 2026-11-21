using System.Globalization;
using System.Numerics;
using MyPcdEditor.Core;
using MyPcdEditor.Core.Grid;
using MyPcdEditor.Core.Merge;
using MyPcdEditor.Core.PointCloud;

namespace MyPcdEditor.Core.Tests;

/// <summary>多包合并：GPS 锚点初值、ICP 精配准、点云拼接与栅格合成。</summary>
public sealed class MergeTests
{
    private static XyzCloud MakeCloud(IEnumerable<Vector3> points)
    {
        List<Vector3> list = points.ToList();
        var data = new byte[list.Count * XyzCloud.Stride];
        for (var i = 0; i < list.Count; i++)
        {
            BitConverter.TryWriteBytes(data.AsSpan(i * XyzCloud.Stride, 4), list[i].X);
            BitConverter.TryWriteBytes(data.AsSpan((i * XyzCloud.Stride) + 4, 4), list[i].Y);
            BitConverter.TryWriteBytes(data.AsSpan((i * XyzCloud.Stride) + 8, 4), list[i].Z);
        }

        return XyzCloud.Create(data, list.Count);
    }

    private static Vector3 PointAt(XyzCloud cloud, int index)
    {
        ReadOnlySpan<byte> bytes = cloud.GetBytes(index * XyzCloud.Stride, XyzCloud.Stride);
        return new Vector3(
            BitConverter.ToSingle(bytes[..4]),
            BitConverter.ToSingle(bytes.Slice(4, 4)),
            BitConverter.ToSingle(bytes.Slice(8, 4)));
    }

    /// <summary>
    /// 一间不对称的房间：地面 + 四壁 + 一根柱子（柱子用来消除 90° 旋转的对称歧义）。
    /// </summary>
    /// <param name="groundCount">地面采样点数。</param>
    /// <param name="wallCount">四面墙合计采样点数。</param>
    /// <remarks>
    /// 点是**随机**撒的，不能改成规则栅格：完美栅格正好是 ICP 的病态输入 —— 初值差一个格距时，
    /// 每个点都能在隔壁格找到「同样好」的对应，最小二乘会稳稳收敛到错一整排的解，
    /// 墙点被 trim 掉之后更是只剩周期性的地面在说话。真实扫描点云没有这种精确周期性。
    /// </remarks>
    private static List<Vector3> Room(float half, float height, int groundCount = 12_000, int wallCount = 6_000)
    {
        var points = new List<Vector3>();
        var random = new Random(20261121);

        float Range(float min, float max) => min + ((float)random.NextDouble() * (max - min));

        for (var i = 0; i < groundCount; i++)
        {
            points.Add(new Vector3(Range(-half, half), Range(-half, half), 0f));
        }

        int perWall = wallCount / 4;
        for (var wall = 0; wall < 4; wall++)
        {
            for (var i = 0; i < perWall; i++)
            {
                float a = Range(-half, half);
                float z = Range(0f, height);
                points.Add(wall switch
                {
                    0 => new Vector3(half, a, z),
                    1 => new Vector3(-half, a, z),
                    2 => new Vector3(a, half, z),
                    _ => new Vector3(a, -half, z),
                });
            }
        }

        // 柱子：竖直的一束点，位置偏离中心，用来锁死 yaw
        for (var i = 0; i < 200; i++)
        {
            points.Add(new Vector3(
                (half * 0.3f) + Range(-0.15f, 0.15f),
                (half * 0.2f) + Range(-0.15f, 0.15f),
                Range(0f, height)));
        }

        return points;
    }

    private static CloudAlignmentOptions FastOptions() => new()
    {
        SourceVoxelSize = 0.1f,
        TargetVoxelSize = 0.1f,
        CoarseCorrespondence = 3f,
        FineCorrespondence = 0.6f,
        MaxIterations = 40,
        InlierDistance = 0.15f,
    };

    [Fact]
    public void Transform_ThenAppliesInOrder()
    {
        var first = AlignmentTransform.FromTranslation(new Vector3(1f, 0f, 0f));
        var second = new AlignmentTransform(MathF.PI / 2f, Vector3.Zero);

        Vector3 moved = first.Then(second).Apply(new Vector3(1f, 0f, 0f));

        Assert.Equal(0f, moved.X, 4);
        Assert.Equal(2f, moved.Y, 4);
    }

    [Fact]
    public void Transform_InverseCancels()
    {
        var transform = new AlignmentTransform(0.7f, new Vector3(3f, -2f, 0.5f));
        Vector3 point = new(12f, -4f, 1.7f);

        Vector3 roundTrip = transform.Inverse().Apply(transform.Apply(point));

        Assert.Equal(point.X, roundTrip.X, 3);
        Assert.Equal(point.Y, roundTrip.Y, 3);
        Assert.Equal(point.Z, roundTrip.Z, 3);
    }

    [Fact]
    public void Anchors_SubtractsSameZoneOrigins()
    {
        var reference = new ProjectedOrigin { X = 342416.112, Y = 3432910.256, Z = 13.864, Zone = 21 };
        var source = new ProjectedOrigin { X = 342607.958, Y = 3432976.777, Z = 14.063, Zone = 21 };

        bool ok = CloudAligner.TryFromAnchors(source, reference, out AlignmentTransform transform, out string? problem);

        Assert.True(ok);
        Assert.Null(problem);

        // UTM 绝对坐标到 float32 只剩 ~3 cm 精度，这里只要求到厘米级
        Assert.Equal(191.846, transform.Translation.X, 1);
        Assert.Equal(66.521, transform.Translation.Y, 1);
        Assert.Equal(0f, transform.Yaw);
    }

    [Fact]
    public void Anchors_RejectDifferentZones()
    {
        var reference = new ProjectedOrigin { X = 1, Y = 1, Z = 0, Zone = 21 };
        var source = new ProjectedOrigin { X = 2, Y = 2, Z = 0, Zone = 22 };

        bool ok = CloudAligner.TryFromAnchors(source, reference, out AlignmentTransform transform, out string? problem);

        Assert.False(ok);
        Assert.Equal(AlignmentTransform.Identity, transform);
        Assert.Contains("带号不同", problem);
    }

    [Fact]
    public void Anchors_RejectMissingOrigin()
    {
        bool ok = CloudAligner.TryFromAnchors(null, null, out _, out string? problem);

        Assert.False(ok);
        Assert.Contains("projected_origin", problem);
    }

    [Fact]
    public void Align_RecoversPureTranslation()
    {
        using XyzCloud target = MakeCloud(Room(10f, 2.5f));

        var truth = AlignmentTransform.FromTranslation(new Vector3(0.6f, -0.4f, 0f));
        AlignmentTransform inverse = truth.Inverse();

        var sourcePoints = new List<Vector3>();
        for (var i = 0; i < target.PointCount; i++)
        {
            sourcePoints.Add(inverse.Apply(PointAt(target, i)));
        }

        using XyzCloud source = MakeCloud(sourcePoints);

        CloudAlignmentResult result = CloudAligner.Align(source, target, AlignmentTransform.Identity, FastOptions());

        Assert.True(result.Converged);
        Assert.Equal(truth.Translation.X, result.Transform.Translation.X, 1);
        Assert.Equal(truth.Translation.Y, result.Transform.Translation.Y, 1);
        Assert.True(result.InlierRatio > 0.9, $"内点率过低：{result.InlierRatio}\n{result.Describe()}");
    }

    [Fact]
    public void Align_RecoversKnownYawAndTranslation()
    {
        // 40×40 m 的厂房尺度，初值误差 ~1 m + 0.5°，与 GPS 锚点的真实误差量级相当
        using XyzCloud target = MakeCloud(Room(20f, 3f));

        var truth = new AlignmentTransform(0.5f * MathF.PI / 180f, new Vector3(0.8f, -0.6f, 0.1f));
        AlignmentTransform inverse = truth.Inverse();

        var sourcePoints = new List<Vector3>();
        for (var i = 0; i < target.PointCount; i++)
        {
            sourcePoints.Add(inverse.Apply(PointAt(target, i)));
        }

        using XyzCloud source = MakeCloud(sourcePoints);

        CloudAlignmentResult result = CloudAligner.Align(source, target, AlignmentTransform.Identity, FastOptions());

        Assert.True(result.Converged);
        Assert.True(result.InlierRatio > 0.8, $"内点率过低：{result.InlierRatio}\n{result.Describe()}");
        Assert.Equal(truth.YawDegrees, result.Transform.YawDegrees, 1);
        Assert.Equal(truth.Translation.X, result.Transform.Translation.X, 1);
        Assert.Equal(truth.Translation.Y, result.Transform.Translation.Y, 1);
        Assert.True(result.FinalRmse < 0.1f, $"残差过大：{result.FinalRmse}");
    }

    [Fact]
    public void Align_IgnoresPointsOutsideOverlap()
    {
        // 巡检总会扫到本次参考包没覆盖的角落（隔壁车间、院子里的车）。这些点在任何目标点
        // 的对应距离之外，只能被当成「无对应」，不能把外推带偏。
        using XyzCloud target = MakeCloud(Room(10f, 2.5f));

        var truth = AlignmentTransform.FromTranslation(new Vector3(0.6f, -0.4f, 0f));
        AlignmentTransform inverse = truth.Inverse();

        var sourcePoints = new List<Vector3>();
        for (var i = 0; i < target.PointCount; i++)
        {
            sourcePoints.Add(inverse.Apply(PointAt(target, i)));
        }

        // 房间上方 8~12 m 层：与参考包任何点的距离都大于粗阶段的对应距离
        var random = new Random(11);
        for (var i = 0; i < 2_000; i++)
        {
            sourcePoints.Add(new Vector3(
                -10f + ((float)random.NextDouble() * 20f),
                -10f + ((float)random.NextDouble() * 20f),
                8f + ((float)random.NextDouble() * 4f)));
        }

        using XyzCloud source = MakeCloud(sourcePoints);

        CloudAlignmentResult result = CloudAligner.Align(source, target, AlignmentTransform.Identity, FastOptions());

        Assert.True(result.Converged, result.Describe());
        Assert.Equal(truth.Translation.X, result.Transform.Translation.X, 1);
        Assert.Equal(truth.Translation.Y, result.Transform.Translation.Y, 1);

        // 内点率的分母是全部源点，多余的 2000 点会如实把比例拉低到 ~90%
        Assert.InRange(result.InlierRatio, 0.85, 0.95);
    }

    [Fact]
    public void Align_ReportsFailureWhenNoOverlap()
    {
        using XyzCloud target = MakeCloud(Room(4f, 2f, 2_000, 1_500));

        var far = new List<Vector3>();
        for (var i = 0; i < target.PointCount; i++)
        {
            far.Add(PointAt(target, i) + new Vector3(500f, 0f, 0f));
        }

        using XyzCloud source = MakeCloud(far);

        CloudAlignmentResult result = CloudAligner.Align(source, target, AlignmentTransform.Identity, FastOptions());

        Assert.False(result.Converged);
        Assert.True(result.InlierRatio < 0.01);
        Assert.Contains("重叠", result.Note);
    }

    [Fact]
    public void Merge_TransformsAndConcatenates()
    {
        using XyzCloud a = MakeCloud([new Vector3(0, 0, 0), new Vector3(1, 0, 0)]);
        using XyzCloud b = MakeCloud([new Vector3(0, 0, 0), new Vector3(1, 0, 0)]);

        using XyzCloud merged = PointCloudMerger.Merge(
        [
            new MergeSource(a, AlignmentTransform.Identity, "a"),
            new MergeSource(b, AlignmentTransform.FromTranslation(new Vector3(10f, 0f, 0f)), "b"),
        ]);

        Assert.Equal(4, merged.PointCount);
        Assert.Equal(10f, PointAt(merged, 2).X, 3);
        Assert.Equal(11f, PointAt(merged, 3).X, 3);
    }

    [Fact]
    public void Merge_DedupeCollapsesCoincidentPoints()
    {
        using XyzCloud a = MakeCloud([new Vector3(0, 0, 0), new Vector3(1, 0, 0)]);
        using XyzCloud b = MakeCloud([new Vector3(0.01f, 0, 0), new Vector3(1.01f, 0, 0)]);

        using XyzCloud merged = PointCloudMerger.Merge(
            [new MergeSource(a, AlignmentTransform.Identity), new MergeSource(b, AlignmentTransform.Identity)],
            dedupeVoxelSize: 0.5f);

        Assert.Equal(2, merged.PointCount);
    }

    [Fact]
    public void GridMerge_UnionsExtentAndPrefersOccupied()
    {
        var free = new PgmImage { Width = 4, Height = 4, MaxValue = 255, Pixels = Enumerable.Repeat((byte)255, 16).ToArray() };
        var occupied = new PgmImage { Width = 4, Height = 4, MaxValue = 255, Pixels = Enumerable.Repeat((byte)0, 16).ToArray() };

        var meta = new MapYaml { Resolution = 1.0, OriginX = 0, OriginY = 0 };

        GridMergeResult result = GridMerger.Merge(
        [
            new GridMergeSource(free, meta, AlignmentTransform.Identity, "a"),
            new GridMergeSource(occupied, meta, AlignmentTransform.FromTranslation(new Vector3(2f, 0f, 0f)), "b"),
        ]);

        Assert.Equal(6, result.Map.Width);
        Assert.Equal(4, result.Map.Height);
        Assert.Equal(0.0, result.Meta.OriginX);
        Assert.Equal(1.0, result.Meta.Resolution);

        // occupied 覆盖第 2~5 列（4 列 × 4 行），free 只剩第 0~1 列没被压掉
        Assert.Equal(255, result.Map[0, 0]);
        Assert.Equal(0, result.Map[2, 0]);
        Assert.Equal(0, result.Map[5, 0]);
        Assert.Equal(16, result.OccupiedCount);
        Assert.Equal(8, result.FreeCount);
        Assert.Equal(8, result.OverlapCount);
    }

    [Fact]
    public void GridMerge_UnknownWinsOverFree()
    {
        var free = new PgmImage { Width = 2, Height = 2, MaxValue = 255, Pixels = [255, 255, 255, 255] };
        var unknown = new PgmImage { Width = 2, Height = 2, MaxValue = 255, Pixels = [205, 205, 205, 205] };
        var meta = new MapYaml { Resolution = 1.0 };

        GridMergeResult result = GridMerger.Merge(
        [
            new GridMergeSource(free, meta, AlignmentTransform.Identity, "a"),
            new GridMergeSource(unknown, meta, AlignmentTransform.Identity, "b"),
        ]);

        Assert.All(result.Map.Pixels, pixel => Assert.Equal(205, pixel));
        Assert.Equal(4, result.OverlapCount);
    }

    [Fact]
    public void MapMerger_EndToEndAlignsTwoPackages()
    {
        string root = Path.Combine(Path.GetTempPath(), "pcdmerge-" + Guid.NewGuid().ToString("N"));
        string referenceDir = Path.Combine(root, "ref");
        string sourceDir = Path.Combine(root, "src");

        try
        {
            Directory.CreateDirectory(referenceDir);
            Directory.CreateDirectory(sourceDir);

            List<Vector3> room = Room(4f, 2f, 2_000, 1_500);
            using XyzCloud cloud = MakeCloud(room);
            PcdWriter.WriteBinaryXyz(Path.Combine(referenceDir, "map.pcd"), cloud);

            // 源包的局部坐标 = 参考点 - 锚点差，合并时应有同样的平移把两者对齐
            var delta = new Vector3(12f, -7f, 0.2f);
            using XyzCloud shifted = MakeCloud(room.Select(p => p - delta));
            PcdWriter.WriteBinaryXyz(Path.Combine(sourceDir, "map.pcd"), shifted);

            WriteOrigin(Path.Combine(referenceDir, ".projected_origin"), 342416.112, 3432910.256, 13.864);
            WriteOrigin(Path.Combine(sourceDir, ".projected_origin"),
                342416.112 + delta.X, 3432910.256 + delta.Y, 13.864 + delta.Z);

            var map = new PgmImage { Width = 8, Height = 8, MaxValue = 255, Pixels = Enumerable.Repeat((byte)255, 64).ToArray() };
            var meta = new MapYaml { Resolution = 1.0, OriginX = -4, OriginY = -4, OccupiedThresh = 0.65, FreeThresh = 0.196 };
            PgmWriter.Write(Path.Combine(referenceDir, "map.pgm"), map);
            PgmWriter.Write(Path.Combine(sourceDir, "map.pgm"), map);
            meta.Save(Path.Combine(referenceDir, "map.yaml"), "map.pgm");
            meta.Save(Path.Combine(sourceDir, "map.yaml"), "map.pgm");

            MapPackage reference = MapPackage.Open(referenceDir);
            MapPackage source = MapPackage.Open(sourceDir);

            MergeOutcome outcome = MapMerger.Merge(reference, source,
                new MergeOptions { DedupeVoxelSize = null, MergeGrids = true });

            using (outcome)
            {
                Assert.Equal(cloud.PointCount * 2, outcome.Cloud.PointCount);
                Assert.True(outcome.Alignment.Converged);
                Assert.True(outcome.Alignment.InlierRatio > 0.9, $"内点率：{outcome.Alignment.InlierRatio}");
                Assert.NotNull(outcome.Grid);
                Assert.Contains("已收敛", outcome.Report);
                Assert.Contains("projected_origin", outcome.Report);
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static void WriteOrigin(string path, double x, double y, double z)
        => File.WriteAllText(path, string.Create(CultureInfo.InvariantCulture,
            $"# projected origin for test\norigin: [{x:F3}, {y:F3}, {z:F3}]\nzone: 21\n"));
}
