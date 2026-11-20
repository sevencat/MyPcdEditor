using System.Globalization;
using Silk.NET.OpenGLES;

namespace MyPcdEditor.Rendering.Gles3;

/// <summary>
/// 一帧渲染结果的像素统计，用于无人工介入的渲染自检：
/// 全黑/全白/着色器没生效/只剩背景色，都能从这几个指标上看出来。
/// </summary>
/// <param name="Width">读回宽度（像素）。</param>
/// <param name="Height">读回高度（像素）。</param>
/// <param name="NonBackgroundRatio">与清屏色不同的像素占比。</param>
/// <param name="DistinctColors">量化到 5 bit/通道后的不同颜色数。</param>
/// <param name="MeanLuminance">全图平均亮度（0..255）。</param>
/// <param name="ColorfulRatio">非背景像素中"彩色"（通道最大差 &gt; 24）的占比。</param>
/// <param name="Rgba">
/// 原始像素（RGBA8，<b>自下而上</b>，与 GL 读回顺序一致）；仅用于落盘核对，可能为 <c>null</c>。
/// </param>
public readonly record struct FramePixelStats(
    int Width,
    int Height,
    double NonBackgroundRatio,
    int DistinctColors,
    double MeanLuminance,
    double ColorfulRatio,
    byte[]? Rgba = null)
{
    /// <summary>读回失败或尺寸非法。</summary>
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>画面里确实画了东西：有足够多的非背景像素，且颜色不是单一值。</summary>
    public bool LooksRendered => !IsEmpty && NonBackgroundRatio >= 0.02 && DistinctColors >= 8;

    public string Describe() => string.Create(
        CultureInfo.InvariantCulture,
        $"{Width}×{Height} 非背景={NonBackgroundRatio:P1} 唯一色={DistinctColors} " +
        $"平均亮度={MeanLuminance:F1} 彩色={ColorfulRatio:P1}");

    /// <summary>
    /// 把当前绑定的帧缓冲读回 CPU 并统计。必须在渲染回调里、FBO 仍绑定时调用。
    /// </summary>
    /// <param name="tolerance">与清屏色每个通道的容差，抵消驱动的舍入差异。</param>
    public static unsafe FramePixelStats Read(
        int width,
        int height,
        byte backgroundR,
        byte backgroundG,
        byte backgroundB,
        int tolerance = 10)
    {
        if (width <= 0 || height <= 0)
        {
            return default;
        }

        int pixelCount = width * height;
        byte[] pixels = new byte[pixelCount * 4];

        GL gl = GlContext.Gl;
        fixed (byte* pointer = pixels)
        {
            // RGBA8 每行 4·width 字节，天然满足 GL_PACK_ALIGNMENT=4，不用改像素打包状态
            gl.ReadPixels(0, 0, (uint)width, (uint)height, PixelFormat.Rgba, PixelType.UnsignedByte, pointer);
        }

        int nonBackground = 0;
        int colorful = 0;
        long luminanceSum = 0;
        var buckets = new bool[32 * 32 * 32];

        for (int i = 0; i < pixels.Length; i += 4)
        {
            int r = pixels[i];
            int g = pixels[i + 1];
            int b = pixels[i + 2];

            luminanceSum += ((r * 299) + (g * 587) + (b * 114)) / 1000;

            if (Math.Abs(r - backgroundR) <= tolerance &&
                Math.Abs(g - backgroundG) <= tolerance &&
                Math.Abs(b - backgroundB) <= tolerance)
            {
                continue;
            }

            nonBackground++;

            int max = Math.Max(r, Math.Max(g, b));
            int min = Math.Min(r, Math.Min(g, b));
            if (max - min > 24)
            {
                colorful++;
            }

            buckets[((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3)] = true;
        }

        int distinct = 0;
        foreach (bool used in buckets)
        {
            if (used)
            {
                distinct++;
            }
        }

        return new FramePixelStats(
            width,
            height,
            nonBackground / (double)pixelCount,
            distinct,
            luminanceSum / (double)pixelCount,
            nonBackground == 0 ? 0 : colorful / (double)nonBackground,
            pixels);
    }
}
