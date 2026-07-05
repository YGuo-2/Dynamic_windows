using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace DynamicIsland.App.SystemInfo;

/// <summary>
/// 电池/电源信息源：纯 Win32 GetSystemPowerStatus 轮询（零 WinRT、零依赖、&lt;1µs syscall）。
/// 台式机（无电池 / BatteryFlag 含 NoBattery / Percent=255）→ Label 恒空，永不接管药丸。
/// </summary>
public sealed class BatterySource : IInfoSource
{
    // Segoe Fluent Icons — char 转换避免 PUA 在源码传输中被吞
    // Battery10 (E83F)，BatteryCharging10 (EA93)
    private static readonly string BatteryGlyph  = ((char)0xE83F).ToString();
    private static readonly string ChargingGlyph = ((char)0xEA93).ToString();

    private readonly DispatcherTimer _timer;
    private string _label = "";
    private string _glyph = "";

    public string Id => "battery";
    public string Label => _label;
    public string Glyph => _glyph;
    public event EventHandler? Changed;

    public BatterySource()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _timer.Tick += (_, _) => Poll();
    }

    public void Start() { Poll(); _timer.Start(); }
    public void Stop()  => _timer.Stop();

    private void Poll()
    {
        string label = "", glyph = "";
        if (GetSystemPowerStatus(out var s))
        {
            // BatteryLifePercent: 0–100 / 255=未知；BatteryFlag bit7(128)=无电池（台式机）
            bool noBattery = (s.BatteryFlag & 128) != 0 || s.BatteryLifePercent == 255;
            if (!noBattery)
            {
                bool charging = s.ACLineStatus == 1;
                glyph = charging ? ChargingGlyph : BatteryGlyph;
                label = $"{s.BatteryLifePercent}%";
            }
        }

        if (label != _label || glyph != _glyph)
        {
            _label = label;
            _glyph = glyph;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;       // 0 离线 / 1 在线 / 255 未知
        public byte BatteryFlag;        // bit7=128 无电池
        public byte BatteryLifePercent; // 0–100 / 255 未知
        public byte SystemStatusFlag;
        public int  BatteryLifeTime;
        public int  BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);
}
