using System.Numerics;
using MyPcdEditor.Core.PointCloud;

namespace MyPcdEditor.Core.Merge;

/// <summary>参与合并的一份点云及其「源局部系 → 参考局部系」的变换。</summary>
public sealed record MergeSource(XyzCloud Cloud, AlignmentTransform Transform, string Name = "");

/// <summary>多份点云拼成一份。</summary>
public static class PointCloudMerger
{
    /// <summary>
    /// 把各份点云按变换搬到参考坐标系后拼接。
    /// </summary>
    /// <param name="dedupeVoxelSize">
    /// 重叠区去重的体素边长；null/0 表示不去重（重叠区点数会翻倍，显示更亮、文件更大）。
    /// </param>
    /// <remarks>
    /// 输出是新的托管数组，不引用任何源文件（ADR-015），所以能直接喂给渲染、导出与后续算子。
    /// 48 M 点的合并结果约 576 MB，去重时还要再翻一倍峰值内存。
    /// </remarks>
    public static XyzCloud Merge(
        IReadOnlyList<MergeSource> sources,
        float? dedupeVoxelSize = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);

        if (sources.Count == 0)
        {
            throw new ArgumentException("至少需要一份点云。", nameof(sources));
        }

        long total = 0;
        foreach (MergeSource source in sources)
        {
            total += source.Cloud.PointCount;
        }

        if (total == 0)
        {
            return XyzCloud.Create([], 0, "合并结果");
        }

        long bytes = total * XyzCloud.Stride;
        if (bytes > int.MaxValue)
        {
            throw new InvalidOperationException(
                $"合并后 {total:N0} 点（{bytes / 1024.0 / 1024.0:F0} MB）超出单数组上限，请先对每个包降采样。");
        }

        var data = new byte[(int)bytes];
        long offset = 0;
        double done = 0;

        foreach (MergeSource source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();

            XyzCloud cloud = source.Cloud;
            AlignmentTransform transform = source.Transform;
            int count = cloud.PointCount;
            int sliceCount = Math.Max(1, Environment.ProcessorCount);
            int writeOffset = (int)offset;

            // Span 不能跨线程捕获：每个分片在自己的委托里重新取一段
            Parallel.For(0, sliceCount, slice =>
            {
                int from = (int)((long)count * slice / sliceCount);
                int to = (int)((long)count * (slice + 1) / sliceCount);
                if (to <= from)
                {
                    return;
                }

                ReadOnlySpan<byte> read = cloud.GetBytes((long)from * XyzCloud.Stride, (to - from) * XyzCloud.Stride);
                Span<byte> write = data.AsSpan(writeOffset + (from * XyzCloud.Stride), (to - from) * XyzCloud.Stride);

                for (int i = from, k = 0; i < to; i++, k++)
                {
                    Vector3 p = VoxelHash.ReadPoint(read, k);
                    if (!VoxelHash.IsFinite(p))
                    {
                        continue;
                    }

                    VoxelHash.WritePoint(write, k, transform.Apply(p));
                }
            });

            offset += (long)count * XyzCloud.Stride;
            done += count;
            progress?.Report(done / total * 0.9);
        }

        XyzCloud merged = XyzCloud.Create(data, (int)total, "合并结果");
        progress?.Report(0.9);

        if (dedupeVoxelSize is not > 0f)
        {
            progress?.Report(1);
            return merged;
        }

        XyzCloud deduped = PointCloudOps.VoxelDownsample(
            merged,
            dedupeVoxelSize.Value,
            progress is null ? null : new Progress<double>(v => progress.Report(0.9 + (v * 0.1))),
            cancellationToken);

        merged.Dispose();
        return deduped;
    }
}
