using System.Numerics;
using System.Runtime.InteropServices;

namespace MyPcdEditor.Core.PointCloud;

/// <summary>一块连续点区间的包围盒，用于粗粒度视锥剔除。</summary>
public readonly record struct ChunkRange(int FirstPoint, int PointCount, Bounds3 Bounds)
{
    /// <summary>结束点索引（不含）。</summary>
    public int EndPoint => FirstPoint + PointCount;

    public bool IsEmpty => PointCount <= 0;
}

/// <summary>
/// 按连续点区间累积分块包围盒。
/// </summary>
/// <remarks>
/// 不按空间切块：点云是零拷贝映射的（ADR-005），空间重排要复制 390 MB 并重写 VBO。
/// 按索引切块零成本——上传时顺路扫一遍已经在手里的字节即可。
/// 代价是一块在空间上是否紧凑取决于 PCD 的点顺序，剔除效果必须实测（见 docs/performance.md）。
/// </remarks>
public sealed class ChunkBoundsBuilder
{
    /// <summary>
    /// 默认每块点数：32 M 点切 496 块。
    /// </summary>
    /// <remarks>
    /// 实测（32.46 M 点，docs/performance.md）：这份数据点顺序几乎没有空间局部性
    /// （4 K 点就横跨 132 m），块越小包围盒越紧、剔除越准：1/2 M 块放大到局部只省 18% 的点，
    /// 64 K 块能省 66%。全图视角下 496 次 draw call 也没测出开销（19.3 vs 17.7 FPS）。
    /// </remarks>
    public const int DefaultChunkPoints = 64 * 1024;

    private readonly int _chunkPoints;
    private readonly int _pointCount;
    private readonly Vector3[] _min;
    private readonly Vector3[] _max;

    public ChunkBoundsBuilder(int pointCount, int chunkPoints = DefaultChunkPoints)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pointCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkPoints);

        _pointCount = pointCount;
        _chunkPoints = chunkPoints;
        ChunkCount = pointCount == 0 ? 0 : (pointCount + chunkPoints - 1) / chunkPoints;

        _min = new Vector3[ChunkCount];
        _max = new Vector3[ChunkCount];
        Array.Fill(_min, new Vector3(float.MaxValue));
        Array.Fill(_max, new Vector3(float.MinValue));
    }

    public int ChunkCount { get; }

    /// <summary>
    /// 扫一遍点云，直接产出分块表。
    /// </summary>
    /// <remarks>
    /// 32 M 点 Debug 下约 1.4 s，必须放在后台线程跑（与 GL 上传并行），不能塞进上传泵，
    /// 否则每片 64 MB 上传后会多卡 200 ms 以上。
    /// </remarks>
    public static ChunkRange[] Build(XyzCloud cloud, int chunkPoints = DefaultChunkPoints, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cloud);

        var builder = new ChunkBoundsBuilder(cloud.PointCount, chunkPoints);
        if (builder.ChunkCount == 0)
        {
            return [];
        }

        // 按块批量扫描，顺便给取消一个检查点
        int sliceBytes = Math.Max(builder._chunkPoints, 1) * XyzCloud.Stride;
        for (long offset = 0; offset < cloud.ByteLength; offset += sliceBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int length = (int)Math.Min(sliceBytes, cloud.ByteLength - offset);
            length -= length % XyzCloud.Stride;
            if (length <= 0)
            {
                break;
            }

            builder.Add(cloud.GetBytes(offset, length), offset);
        }

        return builder.Build(cloud.Bounds);
    }

    /// <summary>累加一片顶点字节对应的包围盒。</summary>
    /// <param name="bytes">紧密排列的 <c>float32 xyz</c> 字节，长度必须是 12 的整数倍。</param>
    /// <param name="byteOffset">这片数据在点云中的字节偏移（必须是 12 的整数倍）。</param>
    public void Add(ReadOnlySpan<byte> bytes, long byteOffset)
    {
        if (ChunkCount == 0 || bytes.Length == 0)
        {
            return;
        }

        if (byteOffset < 0 || byteOffset % XyzCloud.Stride != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(byteOffset), $"偏移 {byteOffset} 不是点的整数倍。");
        }

        if (bytes.Length % XyzCloud.Stride != 0)
        {
            throw new ArgumentException($"长度 {bytes.Length} 不是点的整数倍。", nameof(bytes));
        }

        ReadOnlySpan<float> floats = MemoryMarshal.Cast<byte, float>(bytes);
        long firstPoint = byteOffset / XyzCloud.Stride;
        int count = floats.Length / 3;

        // 按块缓存 min/max：每点少两次数组访问，32 M 点能省下大半秒
        int currentChunk = (int)(firstPoint / _chunkPoints);
        if ((uint)currentChunk >= (uint)ChunkCount)
        {
            return;
        }

        Vector3 min = _min[currentChunk];
        Vector3 max = _max[currentChunk];

        for (int i = 0; i < count; i++)
        {
            int chunk = (int)((firstPoint + i) / _chunkPoints);
            if (chunk != currentChunk)
            {
                _min[currentChunk] = min;
                _max[currentChunk] = max;
                currentChunk = chunk;
                min = _min[chunk];
                max = _max[chunk];
            }

            float x = floats[i * 3];
            float y = floats[(i * 3) + 1];
            float z = floats[(i * 3) + 2];

            // 非有限值不参与包围盒：它们画不出来，却会把包围盒撑到无穷大，让剔除彻底失效
            if (!(MathF.Abs(x) < float.MaxValue) || !(MathF.Abs(y) < float.MaxValue)
                || !(MathF.Abs(z) < float.MaxValue))
            {
                continue;
            }

            min = Vector3.Min(min, new Vector3(x, y, z));
            max = Vector3.Max(max, new Vector3(x, y, z));
        }

        _min[currentChunk] = min;
        _max[currentChunk] = max;
    }

    /// <summary>
    /// 产出分块表。没收到任何点的块用 <paramref name="fallback"/> 兜底（保守：永远参与绘制，不被剔除）。
    /// </summary>
    public ChunkRange[] Build(Bounds3 fallback)
    {
        var result = new ChunkRange[ChunkCount];

        for (int c = 0; c < ChunkCount; c++)
        {
            int first = c * _chunkPoints;
            int count = Math.Min(_chunkPoints, _pointCount - first);
            bool touched = _min[c].X <= _max[c].X;
            result[c] = new ChunkRange(first, count, touched ? new Bounds3(_min[c], _max[c]) : fallback);
        }

        return result;
    }
}
