namespace MyPcdEditor.Core.Grid;

/// <summary>PGM 格式错误。</summary>
public sealed class PgmFormatException(string message) : Exception(message);

/// <summary>
/// 单通道灰度栅格图（PGM P2/P5）。
/// </summary>
/// <remarks>
/// 只保留单通道字节，不扩成 RGBA：样例 32.7 M px，扩成 32 位就是 130 MB（docs/data-format.md §3）。
/// 行序为「图像行 0 在最上」，与 ROS map_server 一致，世界坐标换算交给 <see cref="MapYaml"/>。
/// </remarks>
public sealed class PgmImage
{
    public required int Width { get; init; }

    public required int Height { get; init; }

    public required int MaxValue { get; init; }

    /// <summary>逐行存储，长度 = <see cref="Width"/> × <see cref="Height"/>。</summary>
    public required byte[] Pixels { get; init; }

    public byte this[int x, int y] => Pixels[(y * Width) + x];

    /// <summary>像素总数。</summary>
    public int PixelCount => Width * Height;
}
