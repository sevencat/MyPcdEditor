namespace MyPcdEditor.Core.Grid;

/// <summary>occupancy 栅格的像素语义。</summary>
public enum OccupancyCell
{
    Free,
    Unknown,
    Occupied,
}

/// <summary>
/// PGM 灰度值 → occupancy 语义 → 屏幕配色。
/// </summary>
/// <remarks>
/// <para>语义照 ROS map_server：<c>occ = negate ? v/255 : (255-v)/255</c>，
/// <c>occ &gt; occupied_thresh</c> 为占用、<c>occ &lt; free_thresh</c> 为空闲、其余未知。</para>
/// <para>配色用暗色主题（本工具界面是深色）：占用画亮、空闲接近底色、未知居中，
/// 这样墙体和障碍显眼，空白区不抢眼。颜色同时供图例使用，不要各处硬编码。</para>
/// </remarks>
public static class OccupancyPalette
{
    /// <summary>占用（墙、障碍）。</summary>
    public const byte OccupiedR = 232, OccupiedG = 236, OccupiedB = 242, OccupiedA = 255;

    /// <summary>已知空闲。</summary>
    public const byte FreeR = 30, FreeG = 34, FreeB = 40, FreeA = 255;

    /// <summary>未知（未探索 / 遮挡）。</summary>
    public const byte UnknownR = 74, UnknownG = 80, UnknownB = 92, UnknownA = 255;

    /// <summary>点云叠加色（暖橙，与高度色带的彩色区分开）。</summary>
    public const byte OverlayR = 255, OverlayG = 122, OverlayB = 24, OverlayA = 255;

    /// <summary>
    /// 3D 地面纹理的配色与 alpha：墙白亮不透明、未知与空闲半透明的浅灰蓝。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 不能直接复用 2D 视图那套（空闲 (30,34,40)）：2D 视图里底色是窗口面板灰，深色空闲区看得见；
    /// 3D 场景背景接近纯黑，同样的深灰叠上去和背景分不开，整片地面像不存在。
    /// </para>
    /// <para>
    /// 这里改用浅色系：地面是一块可辨认的灰板，墙是亮白线，点云（饱和彩色）铺在上面依然醒目。
    /// </para>
    /// </remarks>
    public const byte GlOccupiedR = 246, GlOccupiedG = 249, GlOccupiedB = 255, GlOccupiedA = 255;
    public const byte GlFreeR = 96, GlFreeG = 104, GlFreeB = 120, GlFreeA = 140;
    public const byte GlUnknownR = 136, GlUnknownG = 144, GlUnknownB = 162, GlUnknownA = 170;

    public static OccupancyCell Classify(byte value, MapYaml meta)
    {
        ArgumentNullException.ThrowIfNull(meta);
        return Classify(value, meta.Negate, meta.OccupiedThresh, meta.FreeThresh);
    }

    public static OccupancyCell Classify(byte value, bool negate, double occupiedThresh, double freeThresh)
    {
        double occupancy = negate ? value / 255.0 : (255 - value) / 255.0;

        if (occupancy > occupiedThresh)
        {
            return OccupancyCell.Occupied;
        }

        return occupancy < freeThresh ? OccupancyCell.Free : OccupancyCell.Unknown;
    }

    /// <summary>像素 → BGRA 四元组（Avalonia 的 <c>Bgra8888</c> 内存序）。</summary>
    public static (byte B, byte G, byte R, byte A) ToBgra(byte value, MapYaml meta)
        => ToBgra(Classify(value, meta));

    public static (byte B, byte G, byte R, byte A) ToBgra(OccupancyCell cell) => cell switch
    {
        OccupancyCell.Occupied => (OccupiedB, OccupiedG, OccupiedR, OccupiedA),
        OccupancyCell.Free => (FreeB, FreeG, FreeR, FreeA),
        _ => (UnknownB, UnknownG, UnknownR, UnknownA),
    };

    /// <summary>像素 → RGBA 四元组（GL 上传用的内存序），alpha 按类别做半透明。</summary>
    public static (byte R, byte G, byte B, byte A) ToGlRgba(OccupancyCell cell) => cell switch
    {
        OccupancyCell.Occupied => (GlOccupiedR, GlOccupiedG, GlOccupiedB, GlOccupiedA),
        OccupancyCell.Free => (GlFreeR, GlFreeG, GlFreeB, GlFreeA),
        _ => (GlUnknownR, GlUnknownG, GlUnknownB, GlUnknownA),
    };
}
