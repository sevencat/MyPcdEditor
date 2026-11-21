namespace MyPcdEditor.Core.PointCloud;

/// <summary>屏幕矩形选区：归一化视口坐标（0..1，左上原点，<c>Min ≤ Max</c>）。</summary>
/// <remarks>
/// 用归一化坐标而不是像素：判定只需要相机矩阵（已含宽高比），与 DPI 缩放、
/// 渲染目标分辨率都无关，headless 测试里也能算。
/// </remarks>
public readonly record struct ScreenBox(float MinU, float MinV, float MaxU, float MaxV)
{
    /// <summary>由拖动起点与终点构造（拖动方向任意）。</summary>
    public static ScreenBox FromCorners(double x1, double y1, double x2, double y2)
        => new(
            (float)Math.Min(x1, x2),
            (float)Math.Min(y1, y2),
            (float)Math.Max(x1, x2),
            (float)Math.Max(y1, y2));

    public float Width => MaxU - MinU;

    public float Height => MaxV - MinV;

    /// <summary>退化成点或线的框（误点、手抖）不构成选区。</summary>
    public bool IsEmpty => Width <= 0f || Height <= 0f;
}
