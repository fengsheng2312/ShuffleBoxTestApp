using System.IO.Ports;

namespace ShuffleBoxTestApp.Models;

/// <summary>
/// 串口连接参数（需求 F-COM-01 单串口连接、F-SYS-01 串口参数配置）。
/// 后续可从 settings.json 反序列化填充。
/// </summary>
public sealed class SerialSettings
{
    /// <summary>串口号，例如 COM7。</summary>
    public string PortName { get; set; } = string.Empty;

    /// <summary>波特率，主控板默认 115200。</summary>
    public int BaudRate { get; set; } = 115200;

    /// <summary>数据位。</summary>
    public int DataBits { get; set; } = 8;

    /// <summary>停止位。</summary>
    public StopBits StopBits { get; set; } = StopBits.One;

    /// <summary>校验位。</summary>
    public Parity Parity { get; set; } = Parity.None;

    /// <summary>单条指令等待应答的超时时间（毫秒）。F-ERR-01 的重试以此为单次时限。</summary>
    public int ResponseTimeoutMs { get; set; } = 800;

    /// <summary>超时后的额外重试次数（0 表示不重试）。</summary>
    public int CommandRetryCount { get; set; } = 2;

    /// <summary>心跳轮询间隔（毫秒），对应 F-COM-04。</summary>
    public int HeartbeatIntervalMs { get; set; } = 1000;

    /// <summary>意外断开后是否自动重连，对应 F-COM-05。</summary>
    public bool AutoReconnect { get; set; } = true;

    /// <summary>自动重连间隔（毫秒）。</summary>
    public int ReconnectIntervalMs { get; set; } = 2000;

    /// <summary>
    /// 是否拉高 DTR。多数 MCU 方案无关，但部分 USB-TTL 芯片把它当作流控/复位信号，
    /// 连不上时可尝试置 false。
    /// </summary>
    public bool EnableDtr { get; set; } = true;

    /// <summary>是否拉高 RTS。</summary>
    public bool EnableRts { get; set; } = true;

    /// <summary>主控板是否会把下发指令回显回来。为 true 时传输层自动丢弃回显行。</summary>
    public bool FilterCommandEcho { get; set; } = true;

    public override string ToString() => $"{PortName} {BaudRate}-{DataBits}-{Parity.ToString()[0]}-{StopBits}";
}
