using System.Numerics;

namespace MyPcdEditor.Core.PointCloud;

/// <summary>
/// 点 → 体素格子的链式哈希网格，用于 SOR 的近邻搜索。
/// </summary>
/// <remarks>
/// 不建 KD 树：32 M 点的树节点就要几百 MB，而网格只按点数线性分配（槽 + next）。
/// 查询先看 3×3×3，凑不够 k 个再扩到 5×5×5，扩的时候只扫新外壳避免重复。
/// 点用 <see cref="Vector3"/> 数组持有（不是字节流）：SOR 每个点要被邻居反复读，
/// 逐个解字节的开销在这里比多占一份内存更贵。
/// </remarks>
internal sealed class VoxelGridIndex
{
    private long[] _slotKey = [];
    private int[] _head = [];
    private readonly int[] _next;
    private readonly Vector3[] _points;
    private int _mask;
    private int _filled;
    private readonly float _inverseCell;
    private readonly Vector3 _origin;

    public VoxelGridIndex(Vector3 origin, float cellSize, Vector3[] points, int count)
    {
        _origin = origin;
        _inverseCell = 1f / cellSize;
        _points = points;
        _next = new int[count];

        // 先按「每格 8 点」猜槽数；猜小了在插入时按实际占用格数翻倍（见 Grow）
        int capacity = 1024;
        while (capacity < Math.Max(count / 8, 1024))
        {
            capacity <<= 1;
        }

        Allocate(capacity);

        for (var i = 0; i < count; i++)
        {
            Vector3 p = points[i];
            long key = VoxelHash.Pack(
                VoxelHash.Cell(p.X, origin.X, _inverseCell),
                VoxelHash.Cell(p.Y, origin.Y, _inverseCell),
                VoxelHash.Cell(p.Z, origin.Z, _inverseCell));

            int slot = VoxelHash.Hash(key) & _mask;
            while (true)
            {
                long existing = _slotKey[slot];
                if (existing == key)
                {
                    break;
                }

                if (existing < 0)
                {
                    _slotKey[slot] = key;
                    _filled++;
                    break;
                }

                slot = (slot + 1) & _mask;
            }

            _next[i] = _head[slot];
            _head[slot] = i;

            if ((long)_filled * 10 > (long)_slotKey.Length * 7)
            {
                Grow();
            }
        }
    }

    private void Allocate(int capacity)
    {
        _slotKey = new long[capacity];
        _head = new int[capacity];
        _mask = capacity - 1;
        Array.Fill(_slotKey, -1);
        Array.Fill(_head, -1);
    }

    /// <summary>扩容重排：槽里的 key 变了位置，链表头要跟着搬（<c>_next</c> 按点索引，不受影响）。</summary>
    private void Grow()
    {
        long[] oldKeys = _slotKey;
        int[] oldHead = _head;

        Allocate(oldKeys.Length << 1);

        for (var i = 0; i < oldKeys.Length; i++)
        {
            long key = oldKeys[i];
            if (key < 0)
            {
                continue;
            }

            int slot = VoxelHash.Hash(key) & _mask;
            while (_slotKey[slot] >= 0)
            {
                slot = (slot + 1) & _mask;
            }

            _slotKey[slot] = key;
            _head[slot] = oldHead[i];
        }
    }

    /// <summary>
    /// 每个点到最近 <paramref name="k"/> 个点的平均距离（欧氏距离，不是平方）。
    /// </summary>
    /// <param name="found">实际找到的邻居数：0 表示 5×5×5 邻域内空无一物（孤点）。</param>
    public float AverageNearestDistance(int self, int k, float[] distance, int[] indices, out int found)
    {
        Vector3 point = _points[self];

        int cx = VoxelHash.Cell(point.X, _origin.X, _inverseCell);
        int cy = VoxelHash.Cell(point.Y, _origin.Y, _inverseCell);
        int cz = VoxelHash.Cell(point.Z, _origin.Z, _inverseCell);

        int filled = Collect(cx, cy, cz, 1, self, point, distance, indices, 0, k);

        // 注意条件是 filled < k 而不是 0 < filled < k：格子恰好切在数据边缘时，
        // 3×3×3 里可能一个点都没有，这时候才是最需要扩到 5×5×5 的情况
        if (filled < k)
        {
            filled = Collect(cx, cy, cz, 2, self, point, distance, indices, filled, k);
        }

        found = filled;

        if (filled == 0)
        {
            return 0f;
        }

        double sum = 0;
        for (var i = 0; i < filled; i++)
        {
            sum += Math.Sqrt(distance[i]);
        }

        return (float)(sum / filled);
    }

    /// <summary>
    /// 查询任意点的最近邻平方距离（ICP 的对应点匹配用）。
    /// </summary>
    /// <returns>没有邻居时返回 <see cref="float.MaxValue"/>，<paramref name="index"/> 为 -1。</returns>
    /// <remarks>
    /// 只扫 3×3×3：调用方应让格边长 ≥ 可接受的对应距离，此时邻域覆盖 ±1.5 格，
    /// 已经罩住所有「距离 ≤ 格边长」的点；更远的点本来就要按对应距离截断掉，不用找。
    /// </remarks>
    public float NearestSquaredDistance(Vector3 query, out int index)
    {
        index = -1;
        float best = float.MaxValue;

        int cx = VoxelHash.Cell(query.X, _origin.X, _inverseCell);
        int cy = VoxelHash.Cell(query.Y, _origin.Y, _inverseCell);
        int cz = VoxelHash.Cell(query.Z, _origin.Z, _inverseCell);

        for (int z = cz - 1; z <= cz + 1; z++)
        {
            for (int y = cy - 1; y <= cy + 1; y++)
            {
                for (int x = cx - 1; x <= cx + 1; x++)
                {
                    long key = VoxelHash.Pack(x, y, z);
                    int slot = VoxelHash.Hash(key) & _mask;

                    while (true)
                    {
                        long existing = _slotKey[slot];
                        if (existing < 0)
                        {
                            break;
                        }

                        if (existing == key)
                        {
                            for (int i = _head[slot]; i >= 0; i = _next[i])
                            {
                                Vector3 p = _points[i];
                                float dx = p.X - query.X;
                                float dy = p.Y - query.Y;
                                float dz = p.Z - query.Z;
                                float squared = (dx * dx) + (dy * dy) + (dz * dz);

                                if (squared < best)
                                {
                                    best = squared;
                                    index = i;
                                }
                            }

                            break;
                        }

                        slot = (slot + 1) & _mask;
                    }
                }
            }
        }

        return best;
    }

    private int Collect(int cx, int cy, int cz, int ring, int self, Vector3 point,
        float[] distance, int[] indices, int filled, int k)
    {
        for (int z = cz - ring; z <= cz + ring; z++)
        {
            for (int y = cy - ring; y <= cy + ring; y++)
            {
                for (int x = cx - ring; x <= cx + ring; x++)
                {
                    // 扩到第二圈时内层已经查过，只扫新外壳
                    if (ring > 1
                        && Math.Abs(x - cx) < ring
                        && Math.Abs(y - cy) < ring
                        && Math.Abs(z - cz) < ring)
                    {
                        continue;
                    }

                    long key = VoxelHash.Pack(x, y, z);
                    int slot = VoxelHash.Hash(key) & _mask;

                    while (true)
                    {
                        long existing = _slotKey[slot];
                        if (existing < 0)
                        {
                            break;
                        }

                        if (existing == key)
                        {
                            for (int i = _head[slot]; i >= 0; i = _next[i])
                            {
                                if (i == self)
                                {
                                    continue;
                                }

                                filled = Insert(_points[i], point, i, distance, indices, filled, k);
                            }

                            break;
                        }

                        slot = (slot + 1) & _mask;
                    }
                }
            }
        }

        return filled;
    }

    /// <summary>把候选点按平方距离插进「升序 top-k」数组，满了就顶掉最远的。</summary>
    private static int Insert(Vector3 candidate, Vector3 point, int index,
        float[] distance, int[] indices, int filled, int k)
    {
        float dx = candidate.X - point.X;
        float dy = candidate.Y - point.Y;
        float dz = candidate.Z - point.Z;
        float squared = (dx * dx) + (dy * dy) + (dz * dz);

        if (filled == k && squared >= distance[k - 1])
        {
            return filled;
        }

        int position = filled < k ? filled : k - 1;
        while (position > 0 && distance[position - 1] > squared)
        {
            distance[position] = distance[position - 1];
            indices[position] = indices[position - 1];
            position--;
        }

        distance[position] = squared;
        indices[position] = index;
        return Math.Min(filled + 1, k);
    }
}
