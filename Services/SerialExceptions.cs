namespace ShuffleBoxTestApp.Services;

/// <summary>串口链路类异常基类（打开失败、句柄失效、未连接时下发等）。</summary>
public class SerialLinkException : Exception
{
    public SerialLinkException(string message) : base(message) { }

    public SerialLinkException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// 指令应答超时。与链路断开区分开，便于测试层实现“超时重试、连续失败熔断”（F-ERR-01 / F-ERR-02）。
/// </summary>
public class SerialTimeoutException : SerialLinkException
{
    /// <summary>超时的指令原文，便于日志定位。</summary>
    public string Command { get; }

    public SerialTimeoutException(string command, int timeoutMs)
        : base($"指令 \"{command}\" 在 {timeoutMs} ms 内未收到主控板应答")
    {
        Command = command;
    }
}
