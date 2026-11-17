namespace ShuffleBoxTestApp.Models;

/// <summary>
/// 一个串口及其驱动信息，用于界面下拉框展示。
/// 只满足“选对哪个 COM 口”这一诉求，不参与通信过程。
/// </summary>
public sealed record SerialPortInfo
{
    /// <summary>串口号，如 COM7。绑定与打开串口只认这个值。</summary>
    public string PortName { get; init; } = string.Empty;

    /// <summary>设备描述（即设备管理器里的 FriendlyName），如 “USB-SERIAL CH340 (COM9)”。</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>驱动服务名，如 CH343SER、com0com、Serial。可用于区分芯片方案。</summary>
    public string DriverService { get; init; } = string.Empty;

    /// <summary>驱动提供方，如 WCH、Vyacheslav Frolov、(标准端口类型)。</summary>
    public string Manufacturer { get; init; } = string.Empty;

    /// <summary>PnP 设备实例 ID，含 VID/PID，如 USB\VID_1A86&amp;PID_7523\...。</summary>
    public string DeviceId { get; init; } = string.Empty;

    /// <summary>设备状态（OK / Error / Unknown）。</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>驱动配置错误码，0 表示正常。非 0 通常是驱动没装好或被占用。</summary>
    public uint ConfigErrorCode { get; init; }

    /// <summary>驱动信息是否成功取到（WMI 查询失败时为 false，只显示 COM 号）。</summary>
    public bool HasDriverInfo { get; init; }

    /// <summary>设备当前是否可正常使用。</summary>
    public bool IsHealthy => ConfigErrorCode == 0 && Status.Equals("OK", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 驱动摘要，用于下拉框第二行。形如：
    /// “com0com · COM0COM\PORT\CNCB4 · 正常” 或 “CH343SER · VID_1A86&amp;PID_7523 · 驱动异常(码 10)”。
    /// </summary>
    public string DriverSummary
    {
        get
        {
            if (!HasDriverInfo)
            {
                return "驱动信息未取到（系统设备查询不可用）";
            }

            var parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(DriverService))
            {
                parts.Add(DriverService);
            }

            var vidPid = ExtractVidPid(DeviceId);
            if (vidPid is not null)
            {
                parts.Add(vidPid);
            }
            else if (!string.IsNullOrWhiteSpace(DeviceId))
            {
                parts.Add(ShrinkDeviceId(DeviceId));
            }

            parts.Add(ConfigErrorCode == 0
                ? Status.Equals("OK", StringComparison.OrdinalIgnoreCase) ? "正常" : Status
                : $"驱动异常(码 {ConfigErrorCode})");

            return string.Join(" · ", parts);
        }
    }

    /// <summary>下拉框主显示文本。</summary>
    public string Display => string.IsNullOrWhiteSpace(Description)
        ? PortName
        : $"{PortName} · {StripPortSuffix(Description)}";

    /// <summary>把 “com0com - serial port emulator (COM7)” 尾部括号里的口号去掉，避免与前面重复。</summary>
    private static string StripPortSuffix(string description)
    {
        var open = description.LastIndexOf('(');
        if (open > 0 && description.EndsWith(")", StringComparison.Ordinal))
        {
            var inner = description[(open + 1)..^1];
            if (inner.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
            {
                return description[..open].TrimEnd();
            }
        }

        return description.Trim();
    }

    /// <summary>从设备实例 ID 中提取 VID/PID 段，便于识别 CH340/CP210x/FTDI 等芯片。</summary>
    private static string? ExtractVidPid(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            return null;
        }

        var upper = deviceId.ToUpperInvariant();
        var vidIdx = upper.IndexOf("VID_", StringComparison.Ordinal);
        var pidIdx = upper.IndexOf("PID_", StringComparison.Ordinal);

        if (vidIdx < 0 && pidIdx < 0)
        {
            return null;
        }

        var vid = vidIdx >= 0 ? ReadToken(deviceId[(vidIdx + 4)..]) : "?";
        var pid = pidIdx >= 0 ? ReadToken(deviceId[(pidIdx + 4)..]) : "?";
        return $"VID_{vid}&PID_{pid}";
    }

    /// <summary>取到下一个分隔符为止的十六进制段。</summary>
    private static string ReadToken(string tail)
    {
        var end = 0;
        while (end < tail.Length && Uri.IsHexDigit(tail[end]))
        {
            end++;
        }

        return end == 0 ? "?" : tail[..end];
    }

    /// <summary>非 USB 设备实例 ID 通常很长，只保留前两段作为识别线索。</summary>
    private static string ShrinkDeviceId(string deviceId)
    {
        var segments = deviceId.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length switch
        {
            >= 2 => $"{segments[0]}\\{segments[1]}",
            _ => deviceId,
        };
    }

    public override string ToString() => Display;
}
