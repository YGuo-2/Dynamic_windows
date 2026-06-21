using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace DynamicIsland.App;

/// <summary>
/// 连续曲率「squircle」几何：直边 + 四个 Lamé 超椭圆圆角（|x|ⁿ+|y|ⁿ=1，n≈5 即 iOS 圆角手感）。
/// 超椭圆是 squircle 的数学本定义；密采样成多段线，纯色填充下与 figma 的 Bézier 近似视觉等价。
/// 几何在局部坐标 [0,0,w,h]，由调用方按需用作填充或裁剪。
/// </summary>
public static class Squircle
{
    /// <param name="n">超椭圆指数：2=正圆角，~5=iOS squircle，越大越方。</param>
    /// <param name="segPerCorner">每个角采样段数（越多越平滑）。</param>
    public static StreamGeometry BuildGeometry(double w, double h, double radius, double n = 5.0, int segPerCorner = 16)
    {
        double r = Math.Min(radius, Math.Min(w, h) / 2.0);

        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            if (r < 0.5 || w <= 0 || h <= 0) // 退化：直角矩形
            {
                ctx.BeginFigure(new Point(0, 0), true, true);
                ctx.LineTo(new Point(w, 0), true, false);
                ctx.LineTo(new Point(w, h), true, false);
                ctx.LineTo(new Point(0, h), true, false);
            }
            else
            {
                var pts = new List<Point>((segPerCorner + 1) * 4);

                // 一个角：圆心 (cx,cy)，从 startDeg 顺时针扫 90°，超椭圆参数点
                void Corner(double cx, double cy, double startDeg)
                {
                    for (int i = 0; i <= segPerCorner; i++)
                    {
                        double a = (startDeg + 90.0 * i / segPerCorner) * Math.PI / 180.0;
                        double ca = Math.Cos(a), sa = Math.Sin(a);
                        double ex = Math.Sign(ca) * Math.Pow(Math.Abs(ca), 2.0 / n);
                        double ey = Math.Sign(sa) * Math.Pow(Math.Abs(sa), 2.0 / n);
                        pts.Add(new Point(cx + r * ex, cy + r * ey));
                    }
                }

                // 顺时针：右上(-90→0) → 右下(0→90) → 左下(90→180) → 左上(180→270)
                Corner(w - r, r,     -90); // 上边右切点 (w-r,0) → 右边上切点 (w,r)
                Corner(w - r, h - r,   0); // 右边下切点 (w,h-r) → 下边右切点 (w-r,h)
                Corner(r,     h - r,  90); // 下边左切点 (r,h)   → 左边下切点 (0,h-r)
                Corner(r,     r,     180); // 左边上切点 (0,r)   → 上边左切点 (r,0)

                ctx.BeginFigure(pts[0], true, true);
                for (int i = 1; i < pts.Count; i++) ctx.LineTo(pts[i], true, false);
                // 直边由各角之间的 LineTo 与闭合段自动连出（角终点→下一角起点共线）
            }
        }
        g.Freeze();
        return g;
    }

    /// <summary>
    /// squircle 周长近似:直边 + 四角(超椭圆角周长≈同半径圆周)。用于流光描边
    /// StrokeDashArray 的单位换算;亮弧按比例自洽,近似误差对观感无影响,
    /// 故不逐帧 flatten 几何(省 GC)。
    /// </summary>
    public static double Perimeter(double w, double h, double radius)
    {
        double r = Math.Min(radius, Math.Min(w, h) / 2.0);
        if (r < 0.5 || w <= 0 || h <= 0) return 2 * (w + h); // 退化:直角矩形
        return 2 * (w - 2 * r) + 2 * (h - 2 * r) + 2 * Math.PI * r;
    }
}
