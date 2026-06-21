using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace DynamicIsland.App;

/// <summary>
/// SwiftUI 风格阻尼弹簧缓动（参数对齐 spring(response:dampingFraction:)）。
/// EaseInCore(t) 返回归一化位移 0→1；DampingFraction&lt;1 时中段越过 1（overshoot 回弹），=1 临界阻尼无过冲。
/// 末端做归一化校正强制 Ease(1)=1，杜绝 DoubleAnimation 在 t=1 停在残留过冲值上导致的“永久超尺寸”。
/// 使用方须设 EasingMode=EaseIn，让基类原样透传本曲线（默认 EaseOut 会反射翻转曲线）。
/// </summary>
public sealed class SpringEase : EasingFunctionBase
{
    public double Response
    {
        get => (double)GetValue(ResponseProperty);
        set => SetValue(ResponseProperty, value);
    }
    public static readonly DependencyProperty ResponseProperty =
        DependencyProperty.Register(nameof(Response), typeof(double), typeof(SpringEase), new PropertyMetadata(0.3));

    public double DampingFraction
    {
        get => (double)GetValue(DampingFractionProperty);
        set => SetValue(DampingFractionProperty, value);
    }
    public static readonly DependencyProperty DampingFractionProperty =
        DependencyProperty.Register(nameof(DampingFraction), typeof(double), typeof(SpringEase), new PropertyMetadata(0.75));

    /// <summary>动画总时长（秒），把物理时间映射到归一化 t；须与 DoubleAnimation.Duration 一致。</summary>
    public double DurationSeconds
    {
        get => (double)GetValue(DurationSecondsProperty);
        set => SetValue(DurationSecondsProperty, value);
    }
    public static readonly DependencyProperty DurationSecondsProperty =
        DependencyProperty.Register(nameof(DurationSeconds), typeof(double), typeof(SpringEase), new PropertyMetadata(0.3));

    protected override double EaseInCore(double t)
    {
        double zeta = Math.Max(0.0001, DampingFraction);
        double w0 = 2.0 * Math.PI / Math.Max(0.0001, Response);

        // 归一化“剩余位移” y(time)：从 1 衰减到 0；返回 1-y = 已到达比例
        double Y(double time)
        {
            if (zeta < 1.0)
            {
                double wd = w0 * Math.Sqrt(1.0 - zeta * zeta);   // 阻尼角频率
                double env = Math.Exp(-zeta * w0 * time);
                return env * (Math.Cos(wd * time) + (zeta * w0 / wd) * Math.Sin(wd * time));
            }
            double e = Math.Exp(-w0 * time);                     // 临界阻尼，无过冲
            return e * (1.0 + w0 * time);
        }

        double y = Y(t * DurationSeconds);
        double y1 = Y(DurationSeconds);          // 末端残留 → 用线性校正抵消，保证 Ease(1)=1 精确落位
        return 1.0 - y + t * y1;
    }

    protected override Freezable CreateInstanceCore() => new SpringEase();
}
