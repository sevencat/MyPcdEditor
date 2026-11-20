using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Numerics;

namespace MyPcdEditor.Core.PointCloud;

/// <summary>三维包围盒。</summary>
public readonly record struct Bounds3(Vector3 Min, Vector3 Max)
{
    public Vector3 Size => Max - Min;

    public Vector3 Center => (Min + Max) * 0.5f;
}

/// <summary>
/// 只含 XYZ 的点云视图，字节布局固定为紧密排列的 <c>float32 x3</c>（12 字节/点）。
/// </summary>
/// <remarks>
/// <para>
/// 满足零拷贝条件时（见 <see cref="PcdHeader.IsRawXyzBinary"/>）直接映射文件，不构造托管数组：
/// 32M 点的样例有 389 MB，多一份拷贝就是多 389 MB 内存。
/// 否则在加载阶段转成同样的字节布局，上层（GPU 上传）只认这一种形态。
/// </para>
/// <para>坐标系与文件一致，不做任何变换（docs/data-format.md §6）。</para>
/// </remarks>
public sealed unsafe class XyzCloud : IDisposable
{
    /// <summary>每个点的字节数。</summary>
    public const int Stride = 12;

    private readonly MemoryMappedFile? _mmf;
    private readonly MemoryMappedViewAccessor? _accessor;
    private readonly byte[]? _owned;
    private readonly long _dataOffset;
    private byte* _basePointer;
    private bool _disposed;

    private XyzCloud(string sourcePath, int pointCount, Bounds3 bounds, byte[] owned)
    {
        SourcePath = sourcePath;
        PointCount = pointCount;
        Bounds = bounds;
        _owned = owned;
    }

    private XyzCloud(string sourcePath, int pointCount, Bounds3 bounds,
        MemoryMappedFile mmf, MemoryMappedViewAccessor accessor, long dataOffset, byte* pointer)
    {
        SourcePath = sourcePath;
        PointCount = pointCount;
        Bounds = bounds;
        _mmf = mmf;
        _accessor = accessor;
        _dataOffset = dataOffset;
        _basePointer = pointer;
    }

    public string SourcePath { get; }

    public int PointCount { get; }

    public Bounds3 Bounds { get; }

    /// <summary>是否走了文件映射零拷贝路径。</summary>
    public bool IsZeroCopy => _mmf is not null;

    /// <summary>数据段字节数。</summary>
    public long ByteLength => (long)PointCount * Stride;

    /// <summary>按需读取一段紧密排列的 xyz 字节（用于分片上传）。</summary>
    /// <exception cref="ArgumentOutOfRangeException">区间越界。</exception>
    public ReadOnlySpan<byte> GetBytes(long offset, int length)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (offset < 0 || length < 0 || offset + length > ByteLength)
        {
            throw new ArgumentOutOfRangeException(nameof(offset),
                $"区间 [{offset}, {offset + length}) 超出点云范围 0..{ByteLength}。");
        }

        if (_owned is not null)
        {
            return _owned.AsSpan((int)offset, length);
        }

        return new ReadOnlySpan<byte>(_basePointer + _dataOffset + offset, length);
    }

    /// <summary>
    /// 均匀抽样若干点，用于相机适配这类只需要"数据实际分布"的场景。
    /// </summary>
    /// <remarks>
    /// 只用包围盒 8 个角点会严重高估数据范围（角上往往没有点），
    /// 结果就是画面里点云缩成中间一小块。抽样点能把这一偏差消掉。
    /// </remarks>
    public Vector3[] SamplePoints(int maxSamples)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (PointCount == 0 || maxSamples <= 0)
        {
            return [];
        }

        int step = Math.Max(1, PointCount / maxSamples);
        int count = (PointCount + step - 1) / step;
        var points = new Vector3[count];

        for (int i = 0, k = 0; k < count; i += step, k++)
        {
            ReadOnlySpan<byte> p = GetBytes((long)i * Stride, Stride);
            points[k] = new Vector3(
                BitConverter.ToSingle(p[..4]),
                BitConverter.ToSingle(p.Slice(4, 4)),
                BitConverter.ToSingle(p.Slice(8, 4)));
        }

        return points;
    }

    /// <summary>打开 PCD 并转成 XYZ 字节视图（必要时零拷贝）。</summary>
    public static XyzCloud Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        PcdHeader header = PcdHeader.Parse(stream);

        return header.IsRawXyzBinary
            ? OpenZeroCopy(path, header)
            : LoadGeneric(path, header, stream);
    }

    private static XyzCloud OpenZeroCopy(string path, PcdHeader header)
    {
        long expected = (long)header.HeaderLength + (long)header.PointCount * Stride;
        long actual = new FileInfo(path).Length;
        if (actual < expected)
        {
            throw new PcdFormatException(
                $"文件长度 {actual} 小于头部声明所需 {expected}（POINTS={header.PointCount}）。");
        }

        MemoryMappedFile mmf = MemoryMappedFile.CreateFromFile(
            path, FileMode.Open, mapName: null, capacity: 0, MemoryMappedFileAccess.Read);
        MemoryMappedViewAccessor accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

        byte* pointer = null;
        try
        {
            accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            pointer += accessor.PointerOffset;

            var span = new ReadOnlySpan<byte>(pointer + header.HeaderLength, (int)((long)header.PointCount * Stride));
            Bounds3 bounds = ScanBounds(span, header.PointCount, sampleLimit: 500_000);

            return new XyzCloud(path, header.PointCount, bounds, mmf, accessor, header.HeaderLength, pointer);
        }
        catch
        {
            if (pointer is not null)
            {
                accessor.SafeMemoryMappedViewHandle.ReleasePointer();
            }

            accessor.Dispose();
            mmf.Dispose();
            throw;
        }
    }

    private static XyzCloud LoadGeneric(string path, PcdHeader header, Stream stream)
    {
        int count = header.PointCount;
        (int Field, int AosOffset)[] columns = [FindCoordinate(header, "x"), FindCoordinate(header, "y"), FindCoordinate(header, "z")];
        var result = new byte[checked(count * Stride)];
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        switch (header.Data)
        {
            case PcdDataType.Ascii:
            {
                using var reader = new StreamReader(stream, System.Text.Encoding.ASCII, false, 1 << 16, leaveOpen: true);
                var row = 0;
                while (row < count)
                {
                    string? text = reader.ReadLine();
                    if (text is null)
                    {
                        break;
                    }

                    string trimmed = text.Trim();
                    if (trimmed.Length == 0 || trimmed[0] == '#')
                    {
                        continue;
                    }

                    string[] tokens = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (tokens.Length < header.Fields.Length)
                    {
                        throw new PcdFormatException($"第 {row} 行字段数不足：期望 {header.Fields.Length}，实际 {tokens.Length}。");
                    }

                    for (var axis = 0; axis < 3; axis++)
                    {
                        float value = float.Parse(tokens[columns[axis].Field], NumberStyles.Float, CultureInfo.InvariantCulture);
                        WritePoint(result, row, axis, value);
                        Accumulate(ref min, ref max, axis, value);
                    }

                    row++;
                }

                if (row != count)
                {
                    throw new PcdFormatException($"ascii 数据行数不足：期望 {count}，实际 {row}。");
                }

                break;
            }

            case PcdDataType.Binary:
            {
                var aos = new byte[checked(count * header.PointStep)];
                stream.ReadExactly(aos);

                for (var i = 0; i < count; i++)
                {
                    for (var axis = 0; axis < 3; axis++)
                    {
                        float value = BitConverter.ToSingle(aos, i * header.PointStep + columns[axis].AosOffset);
                        WritePoint(result, i, axis, value);
                        Accumulate(ref min, ref max, axis, value);
                    }
                }

                break;
            }

            case PcdDataType.BinaryCompressed:
            {
                Span<byte> sizes = stackalloc byte[8];
                stream.ReadExactly(sizes);
                int compressedSize = BitConverter.ToInt32(sizes[..4]);
                int uncompressedSize = BitConverter.ToInt32(sizes[4..]);

                if (compressedSize <= 0 || uncompressedSize <= 0)
                {
                    throw new PcdFormatException($"binary_compressed 尺寸非法：压缩 {compressedSize} / 解压 {uncompressedSize}。");
                }

                var compressed = new byte[compressedSize];
                stream.ReadExactly(compressed);

                var soa = new byte[uncompressedSize];
                int written = Lzf.Decompress(compressed, soa);
                if (written != uncompressedSize)
                {
                    throw new PcdFormatException($"LZF 解压长度不符：期望 {uncompressedSize}，实际 {written}。");
                }

                // 压缩格式是 SOA：先放完一整列，再放下一列
                int[] soaOffsets = BuildSoaOffsets(header);
                for (var i = 0; i < count; i++)
                {
                    for (var axis = 0; axis < 3; axis++)
                    {
                        int column = columns[axis].Field;
                        int offset = soaOffsets[column] + i * (header.Sizes[column] * header.Counts[column]);
                        float value = BitConverter.ToSingle(soa, offset);
                        WritePoint(result, i, axis, value);
                        Accumulate(ref min, ref max, axis, value);
                    }
                }

                break;
            }

            default:
                throw new PcdFormatException($"不支持的数据类型：{header.Data}。");
        }

        Bounds3 bounds = min.X > max.X
            ? new Bounds3(Vector3.Zero, Vector3.Zero)
            : new Bounds3(min, max);

        return new XyzCloud(path, count, bounds, result);
    }

    private static (int Field, int AosOffset) FindCoordinate(PcdHeader header, string name)
    {
        int field = Array.FindIndex(header.Fields, f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase));
        if (field < 0)
        {
            throw new PcdFormatException($"点云缺少 {name} 字段。");
        }

        if (header.Types[field] != 'F' || header.Sizes[field] != 4 || header.Counts[field] != 1)
        {
            throw new PcdFormatException(
                $"{name} 字段不是单个 float32（TYPE={header.Types[field]} SIZE={header.Sizes[field]} COUNT={header.Counts[field]}）。");
        }

        var offset = 0;
        for (var i = 0; i < field; i++)
        {
            offset += header.Sizes[i] * header.Counts[i];
        }

        return (field, offset);
    }

    private static int[] BuildSoaOffsets(PcdHeader header)
    {
        var offsets = new int[header.Fields.Length];
        var running = 0;
        for (var i = 0; i < header.Fields.Length; i++)
        {
            offsets[i] = running;
            running += header.Sizes[i] * header.Counts[i] * header.PointCount;
        }

        return offsets;
    }

    private static void WritePoint(byte[] target, int index, int axis, float value)
        => BitConverter.TryWriteBytes(target.AsSpan(index * Stride + axis * 4, 4), value);

    private static void Accumulate(ref Vector3 min, ref Vector3 max, int axis, float value)
    {
        if (!float.IsFinite(value))
        {
            return;
        }

        switch (axis)
        {
            case 0:
                min.X = MathF.Min(min.X, value);
                max.X = MathF.Max(max.X, value);
                break;
            case 1:
                min.Y = MathF.Min(min.Y, value);
                max.Y = MathF.Max(max.Y, value);
                break;
            default:
                min.Z = MathF.Min(min.Z, value);
                max.Z = MathF.Max(max.Z, value);
                break;
        }
    }

    /// <summary>抽样统计包围盒（零拷贝时不做全量扫描）。</summary>
    private static Bounds3 ScanBounds(ReadOnlySpan<byte> span, int pointCount, int sampleLimit)
    {
        if (pointCount == 0)
        {
            return new Bounds3(Vector3.Zero, Vector3.Zero);
        }

        int step = Math.Max(1, pointCount / sampleLimit);
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        for (long i = 0; i < pointCount; i += step)
        {
            ReadOnlySpan<byte> p = span.Slice((int)(i * Stride), Stride);
            Accumulate(ref min, ref max, 0, BitConverter.ToSingle(p[..4]));
            Accumulate(ref min, ref max, 1, BitConverter.ToSingle(p[4..8]));
            Accumulate(ref min, ref max, 2, BitConverter.ToSingle(p[8..]));
        }

        return min.X > max.X ? new Bounds3(Vector3.Zero, Vector3.Zero) : new Bounds3(min, max);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_mmf is not null)
        {
            if (_basePointer is not null)
            {
                _accessor!.SafeMemoryMappedViewHandle.ReleasePointer();
                _basePointer = null;
            }

            _accessor!.Dispose();
            _mmf.Dispose();
        }
    }
}
