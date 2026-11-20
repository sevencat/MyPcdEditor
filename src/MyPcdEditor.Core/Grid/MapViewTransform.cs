namespace MyPcdEditor.Core.Grid;

/// <summary>
/// 2D 地图视口的视图/世界坐标变换：视口中心（世界米）+ 每米像素数。
/// </summary>
/// <remarks>
/// <para>屏幕 y 向下、世界 y 向上，两者在这里对齐，别的地方不要再各算一遍。</para>
/// <para>纯数学、不依赖 Avalonia，方便 headless 单测（缩放锚点、往返一致性）。</para>
/// </remarks>
public sealed class MapViewTransform
{
    /// <summary>视口中心的世界坐标（米）。</summary>
    public double CenterX { get; private set; }

    public double CenterY { get; private set; }

    /// <summary>缩放：每米多少屏幕像素。</summary>
    public double PixelsPerMeter { get; private set; } = 1;

    /// <summary>视口尺寸（设备像素）。</summary>
    public double ViewWidth { get; private set; } = 1;

    public double ViewHeight { get; private set; } = 1;

    public bool IsValid => ViewWidth > 0 && ViewHeight > 0 && PixelsPerMeter > 0;

    /// <summary>改视口尺寸（控件尺寸变化时调用）。</summary>
    public void Resize(double width, double height)
    {
        ViewWidth = Math.Max(width, 1);
        ViewHeight = Math.Max(height, 1);
    }

    /// <summary>把世界范围整体框进视口（留 <paramref name="margin"/> 比例边距）。</summary>
    public void FitToWorld(double minX, double minY, double maxX, double maxY, double margin = 0.04)
    {
        double width = Math.Max(maxX - minX, 1e-6);
        double height = Math.Max(maxY - minY, 1e-6);
        double scale = Math.Min(ViewWidth / width, ViewHeight / height);

        CenterX = (minX + maxX) * 0.5;
        CenterY = (minY + maxY) * 0.5;
        PixelsPerMeter = Math.Max(scale * (1 - margin), 1e-6);
    }

    /// <summary>以某个屏幕点为锚点缩放（滚轮缩放时鼠标下的世界点保持不动）。</summary>
    public void ZoomAt(double screenX, double screenY, double factor)
    {
        if (!IsValid || factor <= 0 || double.IsNaN(factor))
        {
            return;
        }

        (double anchorX, double anchorY) = ScreenToWorld(screenX, screenY);

        // 上下限：整图缩到 4 px、或放到 1 像素 = 0.05 mm
        PixelsPerMeter = Math.Clamp(PixelsPerMeter * factor, 1e-3, 2e4);

        (double newX, double newY) = ScreenToWorld(screenX, screenY);
        CenterX += anchorX - newX;
        CenterY += anchorY - newY;
    }

    /// <summary>平移（屏幕像素位移）。</summary>
    public void Pan(double deltaX, double deltaY)
    {
        CenterX -= deltaX / PixelsPerMeter;
        CenterY += deltaY / PixelsPerMeter;
    }

    /// <summary>屏幕坐标 → 世界坐标。</summary>
    public (double X, double Y) ScreenToWorld(double screenX, double screenY)
        => (CenterX + ((screenX - (ViewWidth * 0.5)) / PixelsPerMeter),
            CenterY - ((screenY - (ViewHeight * 0.5)) / PixelsPerMeter));

    /// <summary>世界坐标 → 屏幕坐标。</summary>
    public (double X, double Y) WorldToScreen(double worldX, double worldY)
        => (((worldX - CenterX) * PixelsPerMeter) + (ViewWidth * 0.5),
            ((CenterY - worldY) * PixelsPerMeter) + (ViewHeight * 0.5));

    /// <summary>当前可见的世界范围。</summary>
    public (double MinX, double MinY, double MaxX, double MaxY) VisibleWorld
    {
        get
        {
            (double left, double top) = ScreenToWorld(0, 0);
            (double right, double bottom) = ScreenToWorld(ViewWidth, ViewHeight);
            return (left, bottom, right, top);
        }
    }
}
