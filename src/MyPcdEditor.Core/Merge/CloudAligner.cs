using System.Globalization;
using System.Numerics;
using MyPcdEditor.Core.PointCloud;

namespace MyPcdEditor.Core.Merge;

/// <summary>配准参数。</summary>
public sealed class CloudAlignmentOptions
{
    /// <summary>配准前对源云做的体素降采样（米），只影响配准，不影响输出。</summary>
    public float SourceVoxelSize { get; init; } = 0.5f;

    /// <summary>目标云的体素降采样（米）。</summary>
    public float TargetVoxelSize { get; init; } = 0.25f;

    /// <summary>
    /// 粗配准阶段再降采样到的体素（米）。
    /// </summary>
    /// <remarks>
    /// 有用：巡检点云的地面是近似规则的栅格，粗阶段采样太密时，几度的 yaw 误差会让每个点
    /// 找到「隔壁一列」的点当最近邻，ICP 因此卡在错误的极小值上（错一整排依然很"像"）。
    /// 粗体素把这类周期结构折叠掉，先在大尺度上把旋转对准，精阶段再回到细节。
    /// </remarks>
    public float CoarseVoxelSize { get; init; } = 1.5f;

    /// <summary>粗配准阶段的目标云体素（米），与 <see cref="CoarseVoxelSize"/> 配套。</summary>
    public float CoarseTargetVoxelSize { get; init; } = 1.0f;

    /// <summary>参与求解的源点上限（均匀抽样），控制每轮耗时。</summary>
    public int MaxSourcePoints { get; init; } = 250_000;

    /// <summary>粗配准阶段的最大对应距离（米），要罩得住锚点初值的误差。</summary>
    public float CoarseCorrespondence { get; init; } = 1.5f;

    /// <summary>精配准阶段的最大对应距离（米）。</summary>
    public float FineCorrespondence { get; init; } = 0.6f;

    /// <summary>每阶段最大迭代次数。</summary>
    public int MaxIterations { get; init; } = 25;

    /// <summary>
    /// Huber 权重的拐点，以当前阶段的最大对应距离为单位（0.6 表示 0.6 × 对应距离）。
    /// </summary>
    /// <remarks>
    /// 这里不做「丢掉最差百分之几」的硬截断。原因：地面这类自相似平面上的点无论对齐与否
    /// 都能在附近找到几乎同样好的邻居，残差永远最小；而真正携带修正量的墙面点在错位时
    /// 残差反而是全场最大的。硬截断会精确地裁掉后者、留下前者，迭代于是停在一个
    /// 「残差很小但根本没对齐」的假解上（实测停在中途）。Huber 权重对远点只是降权
    /// （w = 拐点 / 距离），既能压制少量错误对应，又不会把约束直接扔掉。
    /// </remarks>
    public float HuberCutoffRatio { get; init; } = 0.6f;

    /// <summary>收敛判据：单轮平移增量阈值（米）。</summary>
    public float TranslationTolerance { get; init; } = 0.005f;

    /// <summary>收敛判据：单轮 yaw 增量阈值（弧度，5e-4 ≈ 0.03°）。</summary>
    public float YawTolerance { get; init; } = 5e-4f;

    /// <summary>是否估计 yaw；关闭则只求平移（退化场景用）。</summary>
    public bool EstimateYaw { get; init; } = true;

    /// <summary>统计内点比例用的距离阈值（米）。</summary>
    public float InlierDistance { get; init; } = 0.2f;

    internal void Validate()
    {
        if (!(SourceVoxelSize > 0f) || !float.IsFinite(SourceVoxelSize))
        {
            throw new ArgumentOutOfRangeException(nameof(SourceVoxelSize), "源降采样体素必须为正的有限值。");
        }

        if (!(TargetVoxelSize > 0f) || !float.IsFinite(TargetVoxelSize))
        {
            throw new ArgumentOutOfRangeException(nameof(TargetVoxelSize), "目标降采样体素必须为正的有限值。");
        }

        if (MaxSourcePoints < 100)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxSourcePoints), "参与配准的源点至少 100 个。");
        }

        if (!(CoarseCorrespondence > 0f) || !(FineCorrespondence > 0f) || CoarseCorrespondence < FineCorrespondence)
        {
            throw new ArgumentOutOfRangeException(nameof(CoarseCorrespondence), "对应距离必须为正，且粗配准不小于精配准。");
        }

        if (MaxIterations is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxIterations), "迭代次数应在 1~500 之间。");
        }

        if (HuberCutoffRatio is <= 0f or > 1f || !float.IsFinite(HuberCutoffRatio))
        {
            throw new ArgumentOutOfRangeException(nameof(HuberCutoffRatio), "Huber 拐点比例应在 (0, 1] 之间。");
        }
    }
}

/// <summary>一次配准的结果与质量指标。</summary>
public sealed class CloudAlignmentResult
{
    /// <summary>锚点（<c>.projected_origin</c>）给出的初值。</summary>
    public required AlignmentTransform Initial { get; init; }

    /// <summary>精配准后的最终变换（源 → 参考）。</summary>
    public required AlignmentTransform Transform { get; init; }

    public required int Iterations { get; init; }

    /// <summary>初值下的对应点 RMSE（米，加权）：衡量 GPS 锚点本身准不准。</summary>
    public required float InitialRmse { get; init; }

    /// <summary>收敛后的对应点 RMSE（米，加权）。</summary>
    public required float FinalRmse { get; init; }

    /// <summary>最终距离小于 <see cref="CloudAlignmentOptions.InlierDistance"/> 的对应点比例。</summary>
    public required double InlierRatio { get; init; }

    public required int CorrespondenceCount { get; init; }

    public required int SourcePointCount { get; init; }

    public required int TargetPointCount { get; init; }

    /// <summary>是否满足收敛判据。</summary>
    public required bool Converged { get; init; }

    public string Note { get; init; } = string.Empty;

    /// <summary>ICP 相对锚点初值修正了多少。</summary>
    public (float Translation, float YawDegrees) Correction
    {
        get
        {
            Vector3 delta = Transform.Translation - Initial.Translation;
            return (delta.Length(), (Transform.Yaw - Initial.Yaw) * (180f / MathF.PI));
        }
    }

    /// <summary>渲染成多行报告（界面「对齐报告」直接显示）。</summary>
    public string Describe()
    {
        (float corrected, float correctedYaw) = Correction;
        var text = new System.Text.StringBuilder();
        text.AppendLine("GPS 锚点初值：" + Initial.ToString());
        text.AppendLine("精配准结果：" + Transform.ToString());
        text.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"锚点修正量：平移 {corrected:F3} m，yaw {correctedYaw:F3}°"));
        text.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"对应点 RMSE：{InitialRmse:F3} m → {FinalRmse:F3} m（内点率 {InlierRatio * 100:F1}%，{CorrespondenceCount:N0} 对）"));
        text.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"迭代 {Iterations} 轮 · 源 {SourcePointCount:N0} 点 / 目标 {TargetPointCount:N0} 点 · {(Converged ? "已收敛" : "未收敛")}"));

        if (!string.IsNullOrEmpty(Note))
        {
            text.AppendLine(Note);
        }

        return text.ToString();
    }
}

/// <summary>
/// 点云配准：以 <c>.projected_origin</c> 的 UTM 锚点作初值，再跑加权 ICP 消除建图漂移。
/// </summary>
/// <remarks>
/// <para>
/// 只修 4 个自由度（yaw + xyz）：两趟巡检的 roll/pitch 由 IMU 重力对齐，差异是漂移不是姿态，
/// 放开 6DoF 会让平面场景在俯仰方向上滑走。4DoF 的最小二乘有闭式解（2D Procrustes），不用 SVD。
/// </para>
/// <para>
/// 两级由粗到细：先 1.5 m 对应距离把米级偏差拉回来，再 0.6 m 精修。只用一级时，
/// 要么对应距离太大（格内候选点爆炸、慢十倍），要么太小（初值落在对应半径外，原地不动）。
/// </para>
/// </remarks>
public static class CloudAligner
{
    /// <summary>用两个包的 UTM 锚点算初值（同带号才能直接相减）。</summary>
    /// <param name="problem">不可用的原因（锚点缺失/带号不同），失败时非空。</param>
    public static bool TryFromAnchors(
        ProjectedOrigin? source, ProjectedOrigin? reference,
        out AlignmentTransform transform, out string? problem)
    {
        transform = AlignmentTransform.Identity;

        if (source is null || reference is null)
        {
            problem = "缺少 .projected_origin，无法用 GPS 锚点算初值（需要人工对齐或纯 ICP）。";
            return false;
        }

        if (source.Zone <= 0 || reference.Zone <= 0)
        {
            problem = "projected_origin 里没有有效 UTM 带号，无法用 GPS 锚点算初值。";
            return false;
        }

        if (source.Zone != reference.Zone)
        {
            // 跨带时东坐标差到 500 km 量级，直接相减会把点甩到另一个区；正确做法要经经纬度中转，
            // 样例数据不会跨带，先明确报错而不是猜。
            problem = $"两个包的 UTM 带号不同（{reference.Zone} vs {source.Zone}），当前不支持跨带合并。";
            return false;
        }

        problem = null;
        transform = AlignmentTransform.FromTranslation(source.Position - reference.Position);
        return true;
    }

    /// <summary>把 <paramref name="source"/> 配准到 <paramref name="target"/> 的坐标系。</summary>
    /// <param name="initial">初值（通常来自 <see cref="TryFromAnchors"/>）。</param>
    public static CloudAlignmentResult Align(
        XyzCloud source,
        XyzCloud target,
        AlignmentTransform initial,
        CloudAlignmentOptions? options = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        CloudAlignmentOptions opt = options ?? new CloudAlignmentOptions();
        opt.Validate();

        if (source.PointCount == 0 || target.PointCount == 0)
        {
            throw new ArgumentException("配准需要两个非空点云。");
        }

        // 降采样只服务配准：原始云（可能是 MMF 映射）原样不动，合并阶段仍按原精度取点。
        // 先降到精配准精度，粗阶段再从这份结果二次抽稀 —— 32 M 点的降采样要几秒，跑两遍不划算。
        using XyzCloud fineSource = SampleSource(source, opt, cancellationToken);
        using XyzCloud fineTarget = SampleTarget(target, opt, cancellationToken);
        progress?.Report(0.15);

        using XyzCloud coarseSource = PointCloudOps.VoxelDownsample(fineSource, opt.CoarseVoxelSize, null, cancellationToken);
        using XyzCloud coarseTarget = PointCloudOps.VoxelDownsample(fineTarget, opt.CoarseTargetVoxelSize, null, cancellationToken);
        progress?.Report(0.2);

        if (fineSource.PointCount < 6 || fineTarget.PointCount < 6)
        {
            throw new ArgumentException("降采样后点数不足 6，无法配准。");
        }
        double spent = 0.2;
        double remaining = 0.8;

        AlignmentTransform current = initial;
        var totalIterations = 0;
        float initialRmse = 0f;
        var converged = false;
        var pairs = 0;
        var lastRmse = float.NaN;
        int sourceCount = fineSource.PointCount;
        int targetCount = fineTarget.PointCount;

        (XyzCloud Source, XyzCloud Target, float Correspondence)[] levels =
        [
            (coarseSource, coarseTarget, opt.CoarseCorrespondence),
            (fineSource, fineTarget, opt.FineCorrespondence),
        ];

        for (var i = 0; i < levels.Length; i++)
        {
            Vector3[] levelSource = ReadPoints(levels[i].Source);
            Vector3[] levelTarget = ReadPoints(levels[i].Target);

            LevelResult level = RunLevel(
                levelSource, levelTarget, levels[i].Target.Bounds, current,
                levels[i].Correspondence, opt,
                progress, spent, remaining * 0.5, cancellationToken);

            spent += remaining * 0.5;
            remaining *= 0.5;
            totalIterations += level.Iterations;
            pairs = level.Pairs;
            lastRmse = level.Rmse;

            if (i == 0)
            {
                initialRmse = level.FirstRmse;
            }

            current = level.Transform;
            converged = level.Converged;

            if (level.Pairs < 6)
            {
                return new CloudAlignmentResult
                {
                    Initial = initial,
                    Transform = current,
                    Iterations = totalIterations,
                    InitialRmse = initialRmse,
                    FinalRmse = lastRmse,
                    InlierRatio = 0,
                    CorrespondenceCount = level.Pairs,
                    SourcePointCount = sourceCount,
                    TargetPointCount = targetCount,
                    Converged = false,
                    Note = "对应点太少（两个包可能没有重叠区域），已放弃配准，保留 GPS 初值。",
                };
            }
        }

        Vector3[] finalSource = ReadPoints(fineSource);
        Vector3[] finalTarget = ReadPoints(fineTarget);

        (double inlierRatio, int inliers, float rmse) = Evaluate(
            finalSource, finalTarget, fineTarget.Bounds, current, opt.InlierDistance);

        progress?.Report(1);

        return new CloudAlignmentResult
        {
            Initial = initial,
            Transform = current,
            Iterations = totalIterations,
            InitialRmse = initialRmse,
            FinalRmse = rmse,
            InlierRatio = inlierRatio,
            CorrespondenceCount = inliers,
            SourcePointCount = sourceCount,
            TargetPointCount = targetCount,
            Converged = converged,
            Note = BuildNote(converged, inlierRatio, rmse, opt),
        };
    }

    private static string BuildNote(bool converged, double inlierRatio, float rmse, CloudAlignmentOptions opt)
    {
        if (inlierRatio < 0.2)
        {
            return "内点率偏低：两包重叠区域可能很小，对齐结果需要人工确认。";
        }

        if (!converged)
        {
            return $"未在 {opt.MaxIterations * 2} 轮内收敛，结果可能仍有偏差。";
        }

        return rmse > opt.InlierDistance
            ? "残差大于内点阈值，建议检查重叠区或减小「配准体素」。"
            : string.Empty;
    }

    private readonly record struct LevelResult(
        AlignmentTransform Transform, float Rmse, float FirstRmse, int Pairs, int Iterations, bool Converged);

    private static LevelResult RunLevel(
        Vector3[] sourcePoints, Vector3[] targetPoints, Bounds3 targetBounds, AlignmentTransform start,
        float maxCorrespondence, CloudAlignmentOptions opt,
        IProgress<double>? progress, double progressBase, double progressSpan, CancellationToken cancellationToken)
    {
        // 格边长与最大对应距离取一致：3×3×3 覆盖 ±1.5 格，足以覆盖所有 ≤ 该距离的点。
        // 格太大 → 每格候选爆发（慢）；格太小 → 漏对应（收敛到错误的极小）。
        float cell = MathF.Max(maxCorrespondence, 1e-3f);
        var grid = new VoxelGridIndex(targetBounds.Min, cell, targetPoints, targetPoints.Length);

        int count = sourcePoints.Length;
        var correspondence = new Vector3[count];
        var squared = new float[count];
        var weights = new float[count];

        float limitSquared = maxCorrespondence * maxCorrespondence;
        float cutoff = MathF.Max(opt.HuberCutoffRatio * maxCorrespondence, 1e-4f);
        AlignmentTransform current = start;
        float firstRmse = 0f;
        var lastRmse = float.NaN;
        var pairs = 0;
        var converged = false;
        var iterations = 0;

        for (var iter = 0; iter < opt.MaxIterations; iter++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            iterations = iter + 1;

            AlignmentTransform transform = current;

            // 对应点收集可并行：各点写自己的槽位，网格只读
            Parallel.For(0, count, i =>
            {
                Vector3 p = transform.Apply(sourcePoints[i]);
                float d2 = grid.NearestSquaredDistance(p, out int index);

                if (index >= 0 && d2 <= limitSquared)
                {
                    squared[i] = d2;
                    correspondence[i] = targetPoints[index];
                }
                else
                {
                    squared[i] = float.MaxValue;
                }
            });

            var valid = 0;
            for (var i = 0; i < count; i++)
            {
                float d2 = squared[i];
                if (d2 == float.MaxValue)
                {
                    weights[i] = 0f;
                    continue;
                }

                valid++;

                // Huber：拐点以内全权重，以外按拐点/距离衰减，权重永远不会掉到 0
                float d = MathF.Sqrt(d2);
                weights[i] = d <= cutoff ? 1f : cutoff / d;
            }

            if (valid < 6)
            {
                pairs = valid;
                break;
            }

            (bool solved, AlignmentTransform delta, float rmse) =
                Solve(sourcePoints, correspondence, weights, current, opt.EstimateYaw);

            if (!solved)
            {
                pairs = valid;
                break;
            }

            if (iter == 0)
            {
                firstRmse = rmse;
            }

            lastRmse = rmse;
            pairs = valid;
            current = current.Then(delta);

            (float deltaTranslation, float deltaYaw) = delta.Difference(AlignmentTransform.Identity);
            if (deltaTranslation < opt.TranslationTolerance && deltaYaw < opt.YawTolerance)
            {
                converged = true;
                break;
            }
        }

        progress?.Report(progressBase + progressSpan);

        return new LevelResult(current, lastRmse, firstRmse, pairs, iterations, converged);
    }

    /// <summary>
    /// 一步加权最小二乘：对（已变换的源点，对应目标点）闭式求解 yaw + 平移。
    /// </summary>
    /// <remarks>
    /// 2D Procrustes：去质心后 θ = atan2(Σw(p×q)_z, Σw(p·q)_xy)，t = q̄ − R·p̄（质心也按 w 加权）。
    /// 与 SVD 解法等价，但只需两个累加量，也不必处理 3×3 矩阵退化。
    /// 权重为 0 表示该点没有对应，直接跳过。
    /// </remarks>
    private static (bool Solved, AlignmentTransform Delta, float Rmse) Solve(
        Vector3[] sourcePoints, Vector3[] correspondence, float[] weights,
        AlignmentTransform current, bool estimateYaw)
    {
        int count = sourcePoints.Length;

        double px = 0, py = 0, pz = 0;
        double qx = 0, qy = 0, qz = 0;
        double totalWeight = 0;
        double residual = 0;
        var used = 0;

        for (var i = 0; i < count; i++)
        {
            float weight = weights[i];
            if (weight <= 0f)
            {
                continue;
            }

            Vector3 p = current.Apply(sourcePoints[i]);
            Vector3 q = correspondence[i];
            px += weight * p.X;
            py += weight * p.Y;
            pz += weight * p.Z;
            qx += weight * q.X;
            qy += weight * q.Y;
            qz += weight * q.Z;
            residual += weight * Vector3.DistanceSquared(p, q);
            totalWeight += weight;
            used++;
        }

        if (used < 3 || totalWeight <= 0)
        {
            return (false, AlignmentTransform.Identity, 0f);
        }

        var pCenter = new Vector3((float)(px / totalWeight), (float)(py / totalWeight), (float)(pz / totalWeight));
        var qCenter = new Vector3((float)(qx / totalWeight), (float)(qy / totalWeight), (float)(qz / totalWeight));

        double cross = 0;
        double dot = 0;

        for (var i = 0; i < count; i++)
        {
            float weight = weights[i];
            if (weight <= 0f)
            {
                continue;
            }

            Vector3 p = current.Apply(sourcePoints[i]) - pCenter;
            Vector3 q = correspondence[i] - qCenter;
            cross += weight * (((double)p.X * q.Y) - ((double)p.Y * q.X));
            dot += weight * (((double)p.X * q.X) + ((double)p.Y * q.Y));
        }

        float yaw = estimateYaw ? MathF.Atan2((float)cross, (float)dot) : 0f;
        var delta = new AlignmentTransform(yaw, qCenter - AlignmentTransform.RotateY(pCenter, yaw));

        return (true, delta, (float)Math.Sqrt(residual / totalWeight));
    }

    /// <summary>用固定阈值统计内点率与 RMSE（不降权，反映真实一致性）。</summary>
    /// <remarks>
    /// 内点率的分母是<b>全部源点</b>，不是「有对应的点」：两包只有 40% 重叠时，
    /// 用后者会算出 95% 的漂亮数字，掩盖了「一半的点根本没对上」这个事实。
    /// </remarks>
    private static (double InlierRatio, int Pairs, float Rmse) Evaluate(
        Vector3[] sourcePoints, Vector3[] targetPoints, Bounds3 targetBounds,
        AlignmentTransform transform, float inlierDistance)
    {
        float cell = MathF.Max(inlierDistance * 4f, 0.2f);
        var grid = new VoxelGridIndex(targetBounds.Min, cell, targetPoints, targetPoints.Length);

        float limitSquared = inlierDistance * inlierDistance;
        double sumSquares = 0;
        var inliers = 0;

        for (var i = 0; i < sourcePoints.Length; i++)
        {
            Vector3 p = transform.Apply(sourcePoints[i]);
            float d2 = grid.NearestSquaredDistance(p, out int index);
            if (index < 0 || d2 > limitSquared)
            {
                continue;
            }

            inliers++;
            sumSquares += d2;
        }

        if (sourcePoints.Length == 0)
        {
            return (0, 0, float.NaN);
        }

        return ((double)inliers / sourcePoints.Length, inliers,
            (float)Math.Sqrt(sumSquares / Math.Max(inliers, 1)));
    }

    private static XyzCloud SampleSource(XyzCloud cloud, CloudAlignmentOptions opt, CancellationToken cancellationToken)
    {
        XyzCloud sampled = PointCloudOps.VoxelDownsample(cloud, opt.SourceVoxelSize, null, cancellationToken);

        if (sampled.PointCount <= opt.MaxSourcePoints)
        {
            return sampled;
        }

        // 体素降采样后的点按哈希槽顺序排列，近似随机，直接等步长抽即可
        ReadOnlySpan<byte> bytes = sampled.GetBytes(0, checked((int)sampled.ByteLength));
        int step = (sampled.PointCount + opt.MaxSourcePoints - 1) / opt.MaxSourcePoints;
        var count = (sampled.PointCount + step - 1) / step;
        var data = new byte[count * XyzCloud.Stride];

        for (int i = 0, k = 0; k < count; i += step, k++)
        {
            VoxelHash.WritePoint(data, k, VoxelHash.ReadPoint(bytes, i));
        }

        sampled.Dispose();
        return XyzCloud.Create(data, count, cloud.SourcePath);
    }

    private static XyzCloud SampleTarget(XyzCloud cloud, CloudAlignmentOptions opt, CancellationToken cancellationToken)
        => PointCloudOps.VoxelDownsample(cloud, opt.TargetVoxelSize, null, cancellationToken);

    private static Vector3[] ReadPoints(XyzCloud cloud)
    {
        ReadOnlySpan<byte> bytes = cloud.GetBytes(0, checked((int)cloud.ByteLength));
        var points = new Vector3[cloud.PointCount];
        var count = 0;

        for (var i = 0; i < cloud.PointCount; i++)
        {
            Vector3 p = VoxelHash.ReadPoint(bytes, i);
            if (VoxelHash.IsFinite(p))
            {
                points[count++] = p;
            }
        }

        return count == points.Length ? points : points[..count];
    }
}
