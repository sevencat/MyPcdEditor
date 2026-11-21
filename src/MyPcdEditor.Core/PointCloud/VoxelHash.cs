using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MyPcdEditor.Core.PointCloud;

/// <summary>点云算子内部共用的点读写与体素键打包。</summary>
internal static class VoxelHash
{
    /// <summary>每轴 21 位，偏移 2^20（±1M 个体素，0.01 m 体素下覆盖 ±10 km）。</summary>
    private const long Limit = 1L << 20;

    private const long Multiplier = unchecked((long)0x9E3779B97F4A7C15UL);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 ReadPoint(ReadOnlySpan<byte> bytes, int index)
    {
        ref byte start = ref MemoryMarshal.GetReference(bytes.Slice(index * XyzCloud.Stride, XyzCloud.Stride));
        return new Vector3(
            Unsafe.ReadUnaligned<float>(ref start),
            Unsafe.ReadUnaligned<float>(ref Unsafe.Add(ref start, 4)),
            Unsafe.ReadUnaligned<float>(ref Unsafe.Add(ref start, 8)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WritePoint(Span<byte> target, int index, Vector3 point)
    {
        ref byte start = ref MemoryMarshal.GetReference(target.Slice(index * XyzCloud.Stride, XyzCloud.Stride));
        Unsafe.WriteUnaligned(ref start, point.X);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref start, 4), point.Y);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref start, 8), point.Z);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsFinite(Vector3 p)
        => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);

    /// <summary>
    /// 键 → 32 位散列。低位必须有熵：表用 <c>&amp; (capacity-1)</c> 取槽，只留 24 位会让容量超过 2^24
    /// 后一半槽永远用不到（32 M 点云降采样就会撞上），探测链退化成 O(n²)。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Hash(long key)
    {
        ulong x = unchecked((ulong)key * (ulong)Multiplier);
        x ^= x >> 32;
        return (int)x;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Cell(float value, float origin, float inverse)
        => Math.Clamp((int)Math.Floor((value - origin) * inverse), -(int)Limit, (int)Limit);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long Pack(int x, int y, int z)
    {
        long ix = Math.Clamp(x, -(int)Limit, (int)Limit) + Limit;
        long iy = Math.Clamp(y, -(int)Limit, (int)Limit) + Limit;
        long iz = Math.Clamp(z, -(int)Limit, (int)Limit) + Limit;
        return ix | (iy << 21) | (iz << 42);
    }

    /// <summary>点 → 体素键（负值表示超出打包范围，调用方应跳过）。</summary>
    public static long Key(Vector3 point, Vector3 origin, double inverse)
    {
        long ix = (long)Math.Floor((point.X - origin.X) * inverse) + Limit;
        long iy = (long)Math.Floor((point.Y - origin.Y) * inverse) + Limit;
        long iz = (long)Math.Floor((point.Z - origin.Z) * inverse) + Limit;

        if (ix < 0 || iy < 0 || iz < 0 || ix > Limit << 1 || iy > Limit << 1 || iz > Limit << 1)
        {
            return -1;
        }

        return ix | (iy << 21) | (iz << 42);
    }
}

/// <summary>体素 → 质心累加的开放寻址哈希表。</summary>
internal sealed class VoxelAccumulator
{
    private long[] _keys = [];
    private float[] _sumX = [], _sumY = [], _sumZ = [];
    private int[] _counts = [];
    private int _mask;

    public VoxelAccumulator(int expected)
    {
        int capacity = 1024;
        while (capacity < expected)
        {
            capacity <<= 1;
        }

        Allocate(capacity);
    }

    public int Count { get; private set; }

    public void Add(long key, Vector3 point)
    {
        int slot = VoxelHash.Hash(key) & _mask;
        while (true)
        {
            long existing = _keys[slot];
            if (existing == key)
            {
                _sumX[slot] += point.X;
                _sumY[slot] += point.Y;
                _sumZ[slot] += point.Z;
                _counts[slot]++;
                return;
            }

            if (existing < 0)
            {
                _keys[slot] = key;
                _sumX[slot] = point.X;
                _sumY[slot] = point.Y;
                _sumZ[slot] = point.Z;
                _counts[slot] = 1;
                Count++;

                if ((long)Count * 10 > (long)_keys.Length * 7)
                {
                    Grow();
                }

                return;
            }

            slot = (slot + 1) & _mask;
        }
    }

    public void WriteCentroids(Span<byte> target)
    {
        var write = 0;
        for (var slot = 0; slot < _keys.Length; slot++)
        {
            if (_keys[slot] < 0)
            {
                continue;
            }

            int n = _counts[slot];
            VoxelHash.WritePoint(target, write++,
                new Vector3(_sumX[slot] / n, _sumY[slot] / n, _sumZ[slot] / n));
        }
    }

    private void Allocate(int capacity)
    {
        _keys = new long[capacity];
        _sumX = new float[capacity];
        _sumY = new float[capacity];
        _sumZ = new float[capacity];
        _counts = new int[capacity];
        _mask = capacity - 1;
        Array.Fill(_keys, -1);
    }

    private void Grow()
    {
        long[] oldKeys = _keys;
        float[] oldX = _sumX, oldY = _sumY, oldZ = _sumZ;
        int[] oldCounts = _counts;

        Allocate(_keys.Length << 1);

        for (var i = 0; i < oldKeys.Length; i++)
        {
            long key = oldKeys[i];
            if (key < 0)
            {
                continue;
            }

            int slot = VoxelHash.Hash(key) & _mask;
            while (_keys[slot] >= 0)
            {
                slot = (slot + 1) & _mask;
            }

            _keys[slot] = key;
            _sumX[slot] = oldX[i];
            _sumY[slot] = oldY[i];
            _sumZ[slot] = oldZ[i];
            _counts[slot] = oldCounts[i];
        }
    }
}
