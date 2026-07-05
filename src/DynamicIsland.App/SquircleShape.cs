using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;

namespace DynamicIsland.App;

/// <summary>
/// 背景层：用 squircle 连续曲率几何填充自身矩形区域，随尺寸自动重算。
/// 不裁剪、可挂 DropShadowEffect（阴影跟随 squircle 轮廓，避免方形阴影）。
/// </summary>
public sealed class SquircleShape : FrameworkElement
{
    public Brush? Fill
    {
        get => (Brush?)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }
    public static readonly DependencyProperty FillProperty =
        DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(SquircleShape),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Radius
    {
        get => (double)GetValue(RadiusProperty);
        set => SetValue(RadiusProperty, value);
    }
    public static readonly DependencyProperty RadiusProperty =
        DependencyProperty.Register(nameof(Radius), typeof(double), typeof(SquircleShape),
            new FrameworkPropertyMetadata(14.0, FrameworkPropertyMetadataOptions.AffectsRender));

    // 尺寸变化（展开/收起动画逐帧）→ 重画几何
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (Fill is null || ActualWidth <= 0 || ActualHeight <= 0) return;
        dc.DrawGeometry(Fill, null, Squircle.BuildGeometry(ActualWidth, ActualHeight, Radius));
    }
}
