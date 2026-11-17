using System.IO.Ports;
using System.Text;
using ShuffleBoxTestApp.Models;

namespace ShuffleBoxTestApp.Services;

/// <summary>
/// 基于 <see cref="SerialPort"/> 的传输层实现（需求模块① 设备通信）。
///
/// 设计要点：
/// 1. 独立后台线程 + 阻塞 Read 驱动读循环，不用 DataReceived 事件。
///    DataReceived 走线程池且异常难兜，阻塞 Read 更可控；
///    Close 时由 SerialPort.Close() 打断阻塞读并吞掉派生异常。
/// 2. 字节流累积后按 LF 切分成行（容忍 CR），UTF8 解码，避免一次到达的半行被当成一帧。
/// 3. 一问一答：所有下发经 _gate 串行化，同一时刻只有一个待应答指令；
///    取到的第一行归属该指令，其余行按“主控板主动上报”抛给上层（F-COM-03）。
/// 4. 防错配不用 DiscardInBuffer：真实 USB 串口驱动上清接收缓冲会打断正在阻塞的 Read，
///    被误判成链路断开并触发重连死循环。改为“应答形状过滤 + 超时后丢弃一帧迟到应答”。
/// 5. 读循环异常时先检查句柄是否仍打开：仍打开视为瞬时干扰继续读，
///    只有句柄真正失效才判定断开（F-COM-05）。
/// </summary>
public sealed class SerialPortService : ISerialPortService
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    private SerialPort? _port;
    private Thread? _reader;
    private CancellationTokenSource? _loopCts;
    private TaskCompletionSource<string>? _pending;
    private string _pendingCommand = string.Empty;
    private Func<string, bool>? _pendingFilter;
    private bool _dropNextLine;
    private SerialSettings _settings = new();
    private volatile LinkState _state = LinkState.Closed;
    private volatile bool _disposed;

    public event EventHandler<LinkState>? StateChanged;

    public event EventHandler<string>? UnsolicitedLineReceived;

    public event EventHandler<(string Direction, string Text)>? FrameTraced;

    public bool IsOpen => State == LinkState.Open;

    public LinkState State => _state;

    public IReadOnlyList<string> GetAvailablePorts()
    {
        try
        {
            return SerialPort.GetPortNames()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(PortNumber)
                .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            // 枚举注册表失败时返回空列表：界面表现为“无可用串口”，不弹异常。
            return [];
        }
    }

    private static int PortNumber(string portName)
    {
        var digits = new string(portName.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) ? n : int.MaxValue;
    }

    public async Task OpenAsync(SerialSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(SerialPortService));
        }

        if (string.IsNullOrWhiteSpace(settings.PortName))
        {
            throw new SerialLinkException("未选择串口号");
        }

        cancellationToken.ThrowIfCancellationRequested();

        await CloseAsync().ConfigureAwait(false);

        _settings = settings;
        SetState(LinkState.Opening);

        SerialPort? port = null;
        try
        {
            port = new SerialPort(settings.PortName, settings.BaudRate, settings.Parity,
                                  settings.DataBits, settings.StopBits)
            {
                // 由后台线程的阻塞 Read 驱动，不用阈值/事件唤醒，避免半帧被当整帧处理。
                ReadTimeout = SerialPort.InfiniteTimeout,
                WriteTimeout = 2000,
                Encoding = Encoding.UTF8,
                DtrEnable = settings.EnableDtr,
                RtsEnable = settings.EnableRts,
                Handshake = Handshake.None,
            };

            port.Open();

            _loopCts = new CancellationTokenSource();
            _port = port;

            _reader = new Thread(ReadLoop)
            {
                IsBackground = true,
                Name = "SerialReadLoop",
            };
            _reader.Start();

            SetState(LinkState.Open);
        }
        catch (Exception ex)
        {
            port?.Dispose();
            FailPending(new SerialLinkException($"打开串口 {settings.PortName} 失败：{ex.Message}", ex));
            SetState(LinkState.Closed);

            if (ex is SerialLinkException)
            {
                throw;
            }

            throw new SerialLinkException($"打开串口 {settings.PortName} 失败：{ex.Message}", ex);
        }
    }

    public Task CloseAsync()
    {
        var cts = Interlocked.Exchange(ref _loopCts, null);
        var port = Interlocked.Exchange(ref _port, null);
        var reader = Interlocked.Exchange(ref _reader, null);

        cts?.Cancel();

        if (port is not null)
        {
            try
            {
                // Close 会让阻塞中的 Read 抛异常，读循环据此退出。
                if (port.IsOpen)
                {
                    port.Close();
                }
            }
            catch
            {
                // 关闭期派生异常忽略，句柄随后 Dispose 释放。
            }

            port.Dispose();
        }

        if (reader is not null && reader.IsAlive)
        {
            // 收尾有上限，不能无限等待，否则退出界面时卡在关闭流程。
            _ = reader.Join(TimeSpan.FromMilliseconds(500));
        }

        FailPending(new SerialLinkException("串口已关闭"));
        SetState(LinkState.Closed);
        cts?.Dispose();

        return Task.CompletedTask;
    }

    public async Task<string> SendAsync(string command, CancellationToken cancellationToken = default, Func<string, bool>? acceptLine = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(SerialPortService));
        }

        var timeoutMs = _settings.ResponseTimeoutMs > 0 ? _settings.ResponseTimeoutMs : 800;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var port = _port;
            if (port is null || State != LinkState.Open)
            {
                throw new SerialLinkException("串口未连接");
            }

            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            // 登记待应答与形状过滤；不再清接收缓冲（真实驱动上会打断阻塞读导致误判断链）。
            lock (_sync)
            {
                _pending = tcs;
                _pendingCommand = command;
                _pendingFilter = acceptLine;
                _dropNextLine = false;
            }

            try
            {
                port.Write(command + "\r\n");
                FrameTraced?.Invoke(this, ("TX", command));

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(timeoutMs);

                try
                {
                    return await tcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // 超时：标记丢弃下一帧迟到应答，避免错配给下一条指令（不清缓冲）。
                    lock (_sync)
                    {
                        _dropNextLine = true;
                    }
                    throw new SerialTimeoutException(command, timeoutMs);
                }
            }
            finally
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_pending, tcs))
                    {
                        _pending = null;
                        _pendingCommand = string.Empty;
                        _pendingFilter = null;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not SerialLinkException and not SerialTimeoutException
                                   and not OperationCanceledException)
        {
            // 写入期句柄失效说明链路已掉线，以链路异常上报以便触发自动重连。
            MarkFaulted($"下发指令失败：{ex.Message}");
            throw new SerialLinkException($"下发指令 \"{command}\" 失败：{ex.Message}", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> SendWithRetryAsync(string command, CancellationToken cancellationToken = default, Func<string, bool>? acceptLine = null)
    {
        var attempts = Math.Max(0, _settings.CommandRetryCount) + 1;
        SerialTimeoutException? last = null;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                return await SendAsync(command, cancellationToken, acceptLine).ConfigureAwait(false);
            }
            catch (SerialTimeoutException ex) when (attempt < attempts)
            {
                last = ex;
                // 稍作间隔再重试，避免主控板仍在忙时把指令堆进缓冲。
                await Task.Delay(30, cancellationToken).ConfigureAwait(false);
            }
        }

        throw last ?? new SerialTimeoutException(command, _settings.ResponseTimeoutMs);
    }

    public void Write(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        var port = _port;
        if (port is null || State != LinkState.Open)
        {
            throw new SerialLinkException("串口未连接");
        }

        port.Write(command + "\r\n");
        FrameTraced?.Invoke(this, ("TX", command));
    }

    /// <summary>
    /// 读循环：阻塞读 → 累积 → 按行切分 → 分发。
    /// 退出时（含被 Close 打断）把残留半行冲刷出去，避免最后一行丢失。
    /// </summary>
    private void ReadLoop()
    {
        var cts = _loopCts;
        if (cts is null)
        {
            return;
        }

        var token = cts.Token;
        var buffer = new byte[4096];
        var accumulator = new StringBuilder(1024);

        try
        {
            while (!token.IsCancellationRequested)
            {
                var port = _port;
                if (port is null)
                {
                    break;
                }

                int count;
                try
                {
                    count = port.Read(buffer, 0, buffer.Length);
                }
                catch (TimeoutException)
                {
                    continue;
                }
                catch (Exception ex)
                {
                    if (token.IsCancellationRequested || _disposed)
                    {
                        break;
                    }

                    // 句柄仍打开视为瞬时干扰（驱动/清缓冲等），继续读；
                    // 只有句柄真正失效才判定断开，避免误判触发重连死循环。
                    bool stillOpen;
                    try { stillOpen = port.IsOpen; }
                    catch { stillOpen = false; }

                    if (!stillOpen)
                    {
                        MarkFaulted($"串口读取中断：{ex.Message}");
                        return;
                    }

                    continue;
                }

                if (count <= 0)
                {
                    continue;
                }

                accumulator.Append(Encoding.UTF8.GetString(buffer, 0, count));
                FlushCompleteLines(accumulator);
            }
        }
        catch
        {
            // Close/Dispose 期间的派生异常一律忽略，最终状态由 MarkFaulted 或 CloseAsync 决定。
        }

        if (accumulator.Length > 0)
        {
            DispatchLine(accumulator.ToString());
            accumulator.Clear();
        }
    }

    private void FlushCompleteLines(StringBuilder accumulator)
    {
        var text = accumulator.ToString();
        var cut = 0;

        for (var idx = text.IndexOf('\n'); idx >= 0; idx = text.IndexOf('\n', idx + 1))
        {
            var raw = text[cut..idx];
            if (raw.Length > 0 && raw[^1] == '\r')
            {
                raw = raw[..^1];
            }

            DispatchLine(raw);
            cut = idx + 1;
        }

        accumulator.Clear();
        if (cut > 0)
        {
            accumulator.Append(text[cut..]);
        }
    }

    /// <summary>
    /// 一行的归属判定：有待应答指令则作为其应答，否则视为主动上报。
    /// </summary>
    private void DispatchLine(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        var line = raw.Trim();

        lock (_sync)
        {
            // 超时后标记的迟到帧：丢弃一次，防止错配给下一条指令。
            if (_dropNextLine)
            {
                _dropNextLine = false;
                return;
            }

            if (_pending is { } tcs)
            {
                // 部分主控板会回显指令原文，丢弃后继续等真正的应答帧。
                if (_settings.FilterCommandEcho &&
                    string.Equals(line, _pendingCommand, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                // 形状不匹配的视为上一指令的迟到帧，丢弃，继续等真正的应答。
                if (_pendingFilter is { } filter && !filter(line))
                {
                    return;
                }

                _pending = null;
                _pendingCommand = string.Empty;
                _pendingFilter = null;
                FrameTraced?.Invoke(this, ("RX", line));
                tcs.TrySetResult(line);
                return;
            }
        }

        FrameTraced?.Invoke(this, ("RX", line));
        UnsolicitedLineReceived?.Invoke(this, line);
    }

    private void FailPending(Exception ex)
    {
        TaskCompletionSource<string>? tcs;
        lock (_sync)
        {
            tcs = _pending;
            _pending = null;
            _pendingCommand = string.Empty;
            _pendingFilter = null;
        }

        tcs?.TrySetException(ex);
    }

    /// <summary>标记链路失效：让等待中的指令立刻失败，并释放失效句柄供上层重连。</summary>
    private void MarkFaulted(string reason)
    {
        FailPending(new SerialLinkException(reason));
        SetState(LinkState.Faulted);

        var port = Interlocked.Exchange(ref _port, null);
        if (port is not null)
        {
            try
            {
                if (port.IsOpen)
                {
                    port.Close();
                }
            }
            catch
            {
                // 句柄已异常，忽略关闭异常。
            }

            port.Dispose();
        }
    }

    private void SetState(LinkState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        StateChanged?.Invoke(this, state);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        var cts = Interlocked.Exchange(ref _loopCts, null);
        var port = Interlocked.Exchange(ref _port, null);

        cts?.Cancel();

        if (port is not null)
        {
            try
            {
                if (port.IsOpen)
                {
                    port.Close();
                }
            }
            catch
            {
                // 释放阶段忽略异常。
            }

            port.Dispose();
        }

        FailPending(new ObjectDisposedException(nameof(SerialPortService)));
        _gate.Dispose();
        cts?.Dispose();
    }
}
