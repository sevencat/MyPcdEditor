using System.Globalization;
using MyPcdEditor.Core.Grid;
using MyPcdEditor.Core.PointCloud;

namespace MyPcdEditor.Core.Merge;

/// <summary>合并选项。</summary>
public sealed class MergeOptions
{
    /// <summary>点云配准参数。</summary>
    public CloudAlignmentOptions Alignment { get; init; } = new();

    /// <summary>重叠区去重体素（米）；null/0 表示保留两份点。</summary>
    public float? DedupeVoxelSize { get; init; } = 0.05f;

    /// <summary>是否同时合并栅格图。</summary>
    public bool MergeGrids { get; init; } = true;
}

/// <summary>一次「两包合并」的产物。</summary>
public sealed class MergeOutcome : IDisposable
{
    /// <summary>合并后的点云（托管数组，不依赖源文件）。</summary>
    public required XyzCloud Cloud { get; init; }

    public required CloudAlignmentResult Alignment { get; init; }

    /// <summary>合并后的栅格图；两个包缺 PGM/yaml 时为 null。</summary>
    public GridMergeResult? Grid { get; init; }

    public required string ReferenceName { get; init; }

    public required string SourceName { get; init; }

    /// <summary>多行中文报告（界面「对齐报告」直接显示）。</summary>
    public required string Report { get; init; }

    public void Dispose() => Cloud.Dispose();
}

/// <summary>
/// 把两个地图包合成一份：GPS 锚点定初值 → ICP 精配准 → 点云拼接 + 栅格合成。
/// </summary>
/// <remarks>
/// 这是「编排层」，不自己算数学：对齐在 <see cref="CloudAligner"/>，拼接在
/// <see cref="PointCloudMerger"/> / <see cref="GridMerger"/>。放在 Core 里是为了能被无窗口测试覆盖，
/// UI 只负责选目录、显示进度与报告。
/// </remarks>
public static class MapMerger
{
    public static MergeOutcome Merge(
        MapPackage reference,
        MapPackage source,
        MergeOptions? options = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(source);
        MergeOptions opt = options ?? new MergeOptions();

        if (reference.PcdPath is null || source.PcdPath is null)
        {
            throw new InvalidOperationException("两个地图包都要有点云文件（.pcd）才能合并。");
        }

        progress?.Report(0.02);

        using XyzCloud referenceCloud = XyzCloud.Open(reference.PcdPath);
        progress?.Report(0.12);
        cancellationToken.ThrowIfCancellationRequested();

        using XyzCloud sourceCloud = XyzCloud.Open(source.PcdPath);
        progress?.Report(0.25);
        cancellationToken.ThrowIfCancellationRequested();

        bool anchorsUsable = CloudAligner.TryFromAnchors(
            source.ProjectedOrigin, reference.ProjectedOrigin, out AlignmentTransform initial, out string? problem);

        if (!anchorsUsable)
        {
            // 没有锚点时仍尝试纯 ICP，但从 Identity 起步只能修几米，界面要提示用户确认
            initial = AlignmentTransform.Identity;
        }

        CloudAlignmentResult alignment = CloudAligner.Align(
            sourceCloud,
            referenceCloud,
            initial,
            opt.Alignment,
            Divide(progress, 0.25, 0.55),
            cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(0.8);

        var sources = new List<MergeSource>(2)
        {
            new(referenceCloud, AlignmentTransform.Identity, reference.DisplayName),
            new(sourceCloud, alignment.Transform, source.DisplayName),
        };

        XyzCloud merged = PointCloudMerger.Merge(
            sources,
            opt.DedupeVoxelSize,
            Divide(progress, 0.8, 0.95),
            cancellationToken);

        GridMergeResult? grid = null;
        if (opt.MergeGrids)
        {
            grid = TryMergeGrids(reference, source, alignment.Transform, cancellationToken);
        }

        progress?.Report(1);

        long inputPoints = (long)referenceCloud.PointCount + sourceCloud.PointCount;

        return new MergeOutcome
        {
            Cloud = merged,
            Alignment = alignment,
            Grid = grid,
            ReferenceName = reference.DisplayName,
            SourceName = source.DisplayName,
            Report = BuildReport(reference, source, inputPoints, merged.PointCount, alignment, grid, anchorsUsable, problem),
        };
    }

    private static GridMergeResult? TryMergeGrids(
        MapPackage reference, MapPackage source, AlignmentTransform transform, CancellationToken cancellationToken)
    {
        if (reference.PgmPath is null || source.PgmPath is null)
        {
            return null;
        }

        MapYaml referenceMeta = reference.Yaml ?? DefaultMeta();
        MapYaml sourceMeta = source.Yaml ?? DefaultMeta();

        try
        {
            PgmImage referenceMap = PgmReader.Read(reference.PgmPath);
            PgmImage sourceMap = PgmReader.Read(source.PgmPath);

            return GridMerger.Merge(
                [
                    new GridMergeSource(referenceMap, referenceMeta, AlignmentTransform.Identity, reference.DisplayName),
                    new GridMergeSource(sourceMap, sourceMeta, transform, source.DisplayName),
                ],
                null,
                cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or PgmFormatException or UnauthorizedAccessException)
        {
            // 栅格合并不该拖垮点云合并：失败了只丢栅格部分，报告里说明
            return null;
        }
    }

    private static MapYaml DefaultMeta() => new()
    {
        // yaml 缺失时按「像素即原点」兜底，至少不会把图摆到几十米外
        Resolution = 0.05,
        OriginX = 0,
        OriginY = 0,
    };

    private static string BuildReport(
        MapPackage reference, MapPackage source, long inputPoints, int mergedPoints,
        CloudAlignmentResult alignment, GridMergeResult? grid, bool anchorsUsable, string? problem)
    {
        var text = new System.Text.StringBuilder();
        text.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"参考包：{reference.DisplayName}　待合并：{source.DisplayName}"));

        text.AppendLine(anchorsUsable
            ? "初值来源：.projected_origin（UTM 带号一致，直接相减）"
            : $"初值来源：无（{problem}），从原点起步跑纯 ICP");

        text.Append(alignment.Describe());

        text.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"点云：{inputPoints:N0} → {mergedPoints:N0} 点（{(double)mergedPoints / inputPoints * 100:F1}%）"));

        if (grid is null)
        {
            text.AppendLine("栅格：未合并（缺 PGM/yaml 或读取失败）");
        }
        else
        {
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"栅格：{grid.Map.Width}×{grid.Map.Height} @ {grid.Meta.Resolution:F3} m/px，" +
                $"占用 {grid.OccupiedCount:N0} / 空闲 {grid.FreeCount:N0} / 未知 {grid.UnknownCount:N0} px，" +
                $"重叠 {grid.OverlapCount:N0} px"));
        }

        return text.ToString();
    }

    /// <summary>把子任务的 0..1 映射到父进度的某一段。</summary>
    private static IProgress<double>? Divide(IProgress<double>? parent, double from, double span)
        => parent is null ? null : new Progress<double>(v => parent.Report(from + (Math.Clamp(v, 0, 1) * span)));
}
