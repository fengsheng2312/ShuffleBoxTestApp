using ShuffleBoxTestApp.Models;

namespace ShuffleBoxTestApp.Services;

/// <summary>链路状态（对应需求 F-COM-01 / F-COM-05 的状态显示与重连）。</summary>
public enum LinkState
{
    /// <summary>未连接。</summary>
    Closed,

    /// <summary>正在打开串口。</summary>
    Opening,

    /// <summary>已连接（物理层打开成功，是否真正在线由握手结果决定）。</summary>
    Open,

    /// <summary>异常断开（USB 拔出、句柄失效等），可交由上层自动重连。</summary>
    Faulted,
}

/// <summary>
/// 串口传输层抽象。只负责“字节进出 + 按行成帧 + 一问一答配对”，
/// 不理解任何业务指令含义，便于替换为 TCP/模拟实现做单元测试。
/// </summary>
public interface ISerialPortService : IDisposable
{
    /// <summary>链路状态变化（可能在后台线程触发，界面层需自行转派到 UI 线程）。</summary>
    event EventHandler<LinkState>? StateChanged;

    /// <summary>收到主控板主动上报的行（非当前指令的应答）。</summary>
    event EventHandler<string>? UnsolicitedLineReceived;

    /// <summary>收发原文跟踪，用于调试窗口。“TX/RX + 文本”。</summary>
    event EventHandler<(string Direction, string Text)>? FrameTraced;

    /// <summary>链路是否处于 Open 状态。</summary>
    bool IsOpen { get; }

    /// <summary>当前链路状态。</summary>
    LinkState State { get; }

    /// <summary>枚举系统当前可用串口号（已排序去重）。</summary>
    IReadOnlyList<string> GetAvailablePorts();

    /// <summary>打开串口并启动后台读循环。失败抛 <see cref="SerialLinkException"/>。</summary>
    Task OpenAsync(SerialSettings settings, CancellationToken cancellationToken = default);

    /// <summary>关闭串口，停止读循环并释放句柄。幂等。</summary>
    Task CloseAsync();

    /// <summary>
    /// 下发一条指令并等待主控板回一行应答（一问一答）。
    /// 超时抛 <see cref="SerialTimeoutException"/>，链路失效抛 <see cref="SerialLinkException"/>。
    /// 内部串行化，保证同一时刻只有一条指令在等待应答。
    /// </summary>
    Task<string> SendAsync(string command, CancellationToken cancellationToken = default, Func<string, bool>? acceptLine = null);

    /// <summary>下发指令并按配置自动重试超时（F-ERR-01）。全部超时才抛异常。</summary>
    Task<string> SendWithRetryAsync(string command, CancellationToken cancellationToken = default, Func<string, bool>? acceptLine = null);

    /// <summary>只发不等应答，用于批量预下发或广播类指令。</summary>
    void Write(string command);
}
