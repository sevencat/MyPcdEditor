namespace MyPcdEditor.Core.PointCloud;

/// <summary>LZF 解压（PCD <c>binary_compressed</c> 用的就是它）。</summary>
/// <remarks>
/// 与 <c>liblzf</c> 的 <c>lzf_decompress</c> 等价：控制字节 &lt; 32 表示字面量段，
/// 否则高 3 位是长度、低 5 位是回溯距离的高位。不引第三方包，也不实现压缩（本项目只读 PCD）。
/// </remarks>
public static class Lzf
{
    /// <summary>把 <paramref name="input"/> 解压进 <paramref name="output"/>，返回解压字节数。</summary>
    public static int Decompress(ReadOnlySpan<byte> input, Span<byte> output)
    {
        int ip = 0;
        int op = 0;

        while (ip < input.Length)
        {
            uint ctrl = input[ip++];

            if (ctrl < 32)
            {
                // 字面量段：长度 = ctrl + 1
                int len = (int)ctrl + 1;
                if (ip + len > input.Length)
                {
                    throw new PcdFormatException("LZF 输入被截断（字面量段越界）。");
                }

                if (op + len > output.Length)
                {
                    throw new PcdFormatException("LZF 输出缓冲区过小（字面量段越界）。");
                }

                input.Slice(ip, len).CopyTo(output.Slice(op));
                ip += len;
                op += len;
                continue;
            }

            // 回溯段：长度 = (ctrl >> 5)，距离 = ((ctrl & 0x1f) << 8) + next + 1
            int backLen = (int)(ctrl >> 5);
            if (backLen == 7)
            {
                if (ip >= input.Length)
                {
                    throw new PcdFormatException("LZF 输入被截断（长度扩展字节缺失）。");
                }

                backLen += input[ip++];
            }

            if (ip >= input.Length)
            {
                throw new PcdFormatException("LZF 输入被截断（距离字节缺失）。");
            }

            int distance = ((int)(ctrl & 0x1f) << 8) + input[ip++] + 1;
            int reference = op - distance;
            if (reference < 0)
            {
                throw new PcdFormatException("LZF 回溯距离越过输出起点。");
            }

            int copyLen = backLen + 2;
            if (op + copyLen > output.Length)
            {
                throw new PcdFormatException("LZF 输出缓冲区过小（回溯段越界）。");
            }

            // 逐字节复制：源与目的可能重叠，不能用 Span.CopyTo
            for (var i = 0; i < copyLen; i++)
            {
                output[op++] = output[reference + i];
            }
        }

        return op;
    }
}
