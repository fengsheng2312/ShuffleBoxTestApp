namespace ShuffleBoxTestApp.Models;

/// <summary>主控板身份信息（F-COM-02 握手结果）。</summary>
public sealed record BoardIdentity
{
    /// <summary>握手未通过时的占位值。</summary>
    public static BoardIdentity Invalid { get; } = new() { Online = false };

    /// <summary>是否握手成功、主控板在线。</summary>
    public bool Online { get; init; }

    /// <summary>厂商。</summary>
    public string? Vendor { get; init; }

    /// <summary>型号。</summary>
    public string? Model { get; init; }

    /// <summary>主控板序列号。</summary>
    public string? SerialNumber { get; init; }

    /// <summary>固件版本。</summary>
    public string? Firmware { get; init; }

    /// <summary>原始应答，便于协议调试与排查解析问题。</summary>
    public string RawResponse { get; init; } = string.Empty;

    /// <summary>状态栏展示文本。</summary>
    public string Display => Online
        ? string.Join(" / ", new[] { Model, Firmware is null ? null : $"FW {Firmware}" }.Where(s => s is not null))
        : "未握手";
}

/// <summary>主控板心跳状态（F-COM-04）。</summary>
public sealed record BoardStatus
{
    /// <summary>离线/查询失败占位值。</summary>
    public static BoardStatus Offline { get; } = new() { Online = false, Ready = false };

    /// <summary>心跳是否正常。</summary>
    public bool Online { get; init; }

    /// <summary>板卡温度（摄氏度）。</summary>
    public double? Temperature { get; init; }

    /// <summary>供电电压（伏）。</summary>
    public double? Voltage { get; init; }

    /// <summary>就绪标志，false 时不宜下发切换指令。</summary>
    public bool Ready { get; init; }

    /// <summary>原始应答。</summary>
    public string RawResponse { get; init; } = string.Empty;

    /// <summary>状态栏展示文本。</summary>
    public string Display
    {
        get
        {
            if (!Online)
            {
                return "心跳丢失";
            }

            var parts = new List<string>();
            if (Temperature is { } t)
            {
                parts.Add($"{t:0.0}℃");
            }

            if (Voltage is { } v)
            {
                parts.Add($"{v:0.00}V");
            }

            parts.Add(Ready ? "就绪" : "忙");
            return string.Join("  ", parts);
        }
    }
}
