using System.Threading.Channels;
using ShuffleBoxTestApp.Models;

namespace ShuffleBoxTestApp.Services;

/// <summary>
/// 主控板业务服务：把“字节链路”提升为“有身份、有心跳、能下发业务指令”的设备对象。
/// 覆盖需求 F-COM-02 握手、F-COM-03 指令封装、F-COM-04 心跳、F-COM-05 自动重连。
/// </summary>
public interface IMainBoardService : IDisposable
{
    /// <summary>链路状态变化（后台线程触发）。</summary>
    event EventHandler<LinkState>? StateChanged;

    /// <summary>握手身份变化（后台线程触发）。</summary>
    event EventHandler<BoardIdentity>? IdentityChanged;

    /// <summary>心跳状态变化（后台线程触发）。</summary>
    event EventHandler<BoardStatus>? StatusChanged;

    /// <summary>主控板主动上报的行，界面按日志展示。</summary>
    event EventHandler<string>? UnsolicitedLine;

    /// <summary>收发原文，界面调试面板使用。</summary>
    event EventHandler<(string Direction, string Text)>? FrameTraced;

    BoardIdentity Identity { get; }

    BoardStatus Status { get; }

    LinkState State { get; }

    /// <summary>打开串口并握手。握手失败不回滚连接，由 <see cref="Identity"/> 反映未在线。</summary>
    Task ConnectAsync(SerialSettings settings, CancellationToken cancellationToken = default);

    /// <summary>主动断开（用户点击“断开”）。会停止自动重连。</summary>
    Task DisconnectAsync();

    /// <summary>可选 *IDN? 握手。当前设备不支持，连接流程默认不调用，仅保留供支持该指令的板卡使用。</summary>
    Task<BoardIdentity> HandshakeAsync(CancellationToken cancellationToken = default);

    /// <summary>可选 SYS:STAT? 状态查询。当前设备不支持，默认不调用。</summary>
    Task<BoardStatus> QueryStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>切换光开关（SMSCH mems_id mems_ch）。memsId 1~16，memsCh 1~64。</summary>
    Task SwitchMemsAsync(int memsId, int memsCh, CancellationToken cancellationToken = default);

    /// <summary>读取一次 PD 原始 ADC 值（GPDADC pd_id）。pdId 1~16。</summary>
    Task<int> ReadPdAsync(int pdId = 1, CancellationToken cancellationToken = default);

    /// <summary>
    /// 查询左通道经 Shuffle Box 后实际到达的右通道号；主控板不支持或解析失败时返回 0。
    /// </summary>
    Task<int> QueryActualRightAsync(int leftChannel, CancellationToken cancellationToken = default);

    /// <summary>扫描期间挂起心跳，避免心跳指令与测试指令抢占同一条串口。</summary>
    void SetHeartbeatSuspended(bool suspended);

    /// <summary>列出可用串口。</summary>
    IReadOnlyList<string> GetAvailablePorts();
}

/// <inheritdoc />
public sealed class MainBoardService : IMainBoardService
{
    private readonly ISerialPortService _port;
    private readonly MainBoardProtocol _protocol;
    private readonly ILogService _log;

    private readonly object _sync = new();
    private BoardIdentity _identity = BoardIdentity.Invalid;
    private BoardStatus _status = BoardStatus.Offline;
    private bool _heartbeatSuspended;
    private volatile bool _userWantsConnected;
    private int _reconnectFailures;
    private volatile bool _disposed;

    private CancellationTokenSource? _reconnectCts;
    private CancellationTokenSource? _heartbeatCts;
    private Task? _reconnectLoop;
    private Task? _heartbeatLoop;
    private SerialSettings _settings = new();

    /// <summary>心跳连续失败达到该次数即认定心跳丢失（连续失败熔断思想，F-ERR-02）。</summary>
    private const int HeartbeatFailureBudget = 3;

    public MainBoardService(ISerialPortService port, MainBoardProtocol protocol, ILogService log)
    {
        _port = port;
        _protocol = protocol;
        _log = log;

        _port.StateChanged += OnPortStateChanged;
        _port.UnsolicitedLineReceived += OnUnsolicitedLine;
        _port.FrameTraced += OnFrameTraced;
    }

    public event EventHandler<LinkState>? StateChanged;

    public event EventHandler<BoardIdentity>? IdentityChanged;

    public event EventHandler<BoardStatus>? StatusChanged;

    public event EventHandler<string>? UnsolicitedLine;

    public event EventHandler<(string Direction, string Text)>? FrameTraced;

    public BoardIdentity Identity
    {
        get { lock (_sync) return _identity; }
    }

    public BoardStatus Status
    {
        get { lock (_sync) return _status; }
    }

    public LinkState State => _port.State;

    public IReadOnlyList<string> GetAvailablePorts() => _port.GetAvailablePorts();

    public async Task ConnectAsync(SerialSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _settings = settings;
        _userWantsConnected = true;
        _reconnectFailures = 0;

        await _port.OpenAsync(settings, cancellationToken).ConfigureAwait(false);

        // 设备仅支持 SMSCH/GPDADC，无 *IDN? 握手：串口打开即视为已连接、可直接下发指令。
        SetIdentity(new BoardIdentity { Online = true, RawResponse = "串口已打开（设备无握手指令）" });
        SetStatus(new BoardStatus { Online = true, Ready = true });
        _log.Info($"串口 {settings.PortName} 已连接，可直接下发 SMSCH / GPDADC 指令。");

        StartHeartbeat();
        StartReconnectLoop();
    }

    public async Task DisconnectAsync()
    {
        _userWantsConnected = false;
        StopHeartbeat();

        var cts = Interlocked.Exchange(ref _reconnectCts, null);
        cts?.Cancel();
        cts?.Dispose();

        await _port.CloseAsync().ConfigureAwait(false);
        SetIdentity(BoardIdentity.Invalid);
        SetStatus(BoardStatus.Offline);
    }

    public async Task<BoardIdentity> HandshakeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var reply = await _port.SendWithRetryAsync(_protocol.Identify, cancellationToken).ConfigureAwait(false);
            var identity = _protocol.ParseIdentity(reply);

            if (identity.Online)
            {
                _log.Info($"主控板握手成功：{identity.Display}（原始应答 {reply}）");
            }
            else
            {
                _log.Warn($"主控板握手应答无法解析：{reply}");
            }

            SetIdentity(identity);
            return identity;
        }
        catch (Exception ex) when (ex is SerialLinkException or OperationCanceledException)
        {
            if (ex is OperationCanceledException)
            {
                throw;
            }

            _log.Warn($"主控板握手失败：{ex.Message}");
            SetIdentity(BoardIdentity.Invalid);
            return BoardIdentity.Invalid;
        }
    }

    public async Task<BoardStatus> QueryStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var reply = await _port.SendAsync(_protocol.QueryStatus, cancellationToken).ConfigureAwait(false);
            var status = _protocol.ParseStatus(reply);
            SetStatus(status);
            return status;
        }
        catch (SerialLinkException)
        {
            SetStatus(BoardStatus.Offline);
            throw;
        }
    }

    public async Task SwitchMemsAsync(int memsId, int memsCh, CancellationToken cancellationToken = default)
    {
        if (memsId is < MainBoardProtocol.MemsIdMin or > MainBoardProtocol.MemsIdMax)
        {
            throw new SerialLinkException($"mems_id 超出范围（{MainBoardProtocol.MemsIdMin}~{MainBoardProtocol.MemsIdMax}）：{memsId}");
        }

        if (memsCh is < MainBoardProtocol.MemsChMin or > MainBoardProtocol.MemsChMax)
        {
            throw new SerialLinkException($"mems_ch 超出范围（{MainBoardProtocol.MemsChMin}~{MainBoardProtocol.MemsChMax}）：{memsCh}");
        }

        var reply = await _port.SendWithRetryAsync(_protocol.SwitchMems(memsId, memsCh), cancellationToken, line => line.StartsWith("OK", StringComparison.OrdinalIgnoreCase) || _protocol.IsError(line))
                                .ConfigureAwait(false);

        if (_protocol.IsError(reply))
        {
            throw new SerialLinkException($"光开关切换被主控板拒绝：{reply}");
        }
    }

    public async Task<int> ReadPdAsync(int pdId = 1, CancellationToken cancellationToken = default)
    {
        if (pdId is < MainBoardProtocol.PdIdMin or > MainBoardProtocol.PdIdMax)
        {
            throw new SerialLinkException($"pd_id 超出范围（{MainBoardProtocol.PdIdMin}~{MainBoardProtocol.PdIdMax}）：{pdId}");
        }

        var reply = await _port.SendAsync(_protocol.ReadPd(pdId), cancellationToken, line => line.Contains("ADC:", StringComparison.OrdinalIgnoreCase) || _protocol.IsError(line)).ConfigureAwait(false);

        if (_protocol.IsError(reply))
        {
            throw new SerialLinkException($"PD 读取被主控板拒绝：{reply}");
        }

        if (!_protocol.TryParsePdAdc(reply, out var adc))
        {
            throw new SerialLinkException($"PD 应答无法解析：{reply}");
        }

        return adc;
    }

    public async Task<int> QueryActualRightAsync(int leftChannel, CancellationToken cancellationToken = default)
    {
        var reply = await _port.SendAsync(_protocol.QueryActualRight(leftChannel), cancellationToken)
                                .ConfigureAwait(false);

        return _protocol.TryParseRightChannel(reply, out var right) ? right : 0;
    }

    public void SetHeartbeatSuspended(bool suspended)
    {
        lock (_sync)
        {
            _heartbeatSuspended = suspended;
        }
    }

    /// <summary>
    /// Link-loss decision driven by the heartbeat probe.
    /// Verified on real hardware: when the board is unplugged or the peer program exits,
    /// the Windows serial driver often does NOT make the blocking Read throw, so the link
    /// would stay in a fake "Open" state forever. The heartbeat is therefore authoritative:
    /// after N silent rounds we close the port so the state is truthful, and let the
    /// reconnect loop rebuild the link (requirement F-COM-05).
    /// </summary>
    private async Task HandleHeartbeatLossAsync()
    {
        _log.Warn($"heartbeat silent for {HeartbeatFailureBudget} rounds, link considered lost");

        SetIdentity(BoardIdentity.Invalid);
        SetStatus(BoardStatus.Offline);

        await _port.CloseAsync().ConfigureAwait(false);

        if (_userWantsConnected && _settings.AutoReconnect)
        {
            StartReconnectLoop();
        }
    }

    private void OnPortStateChanged(object? sender, LinkState state)
    {
        StateChanged?.Invoke(this, state);

        if (state is LinkState.Closed or LinkState.Opening)
        {
            return;
        }

        if (state == LinkState.Faulted)
        {
            SetStatus(BoardStatus.Offline);
            SetIdentity(BoardIdentity.Invalid);
            _log.Warn("串口链路异常断开。");

            // 意外断开时拉起自动重连（F-COM-05），用户主动断开不会重连。
            if (_userWantsConnected && _settings.AutoReconnect)
            {
                StartReconnectLoop();
            }
        }
    }

    private void OnUnsolicitedLine(object? sender, string line) => UnsolicitedLine?.Invoke(this, line);

    private void OnFrameTraced(object? sender, (string Direction, string Text) frame) => FrameTraced?.Invoke(this, frame);

    private void StartHeartbeat()
    {
        // Always restart. A previous loop can still be running when ConnectAsync is called
        // again right after DisconnectAsync: cancellation is cooperative, so the old task
        // may not have observed it yet. Returning early in that window would leave the new
        // session with no heartbeat at all, and the link-loss probe would never run.
        StopHeartbeat();

        _heartbeatCts = new CancellationTokenSource();
        var token = _heartbeatCts.Token;

        _heartbeatLoop = Task.Run(async () =>
        {
            var failures = 0;
            var interval = Math.Max(200, _settings.HeartbeatIntervalMs);

            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(interval, token).ConfigureAwait(false);

                    if (token.IsCancellationRequested || !_port.IsOpen)
                    {
                        continue;
                    }

                    bool suspended;
                    lock (_sync)
                    {
                        suspended = _heartbeatSuspended;
                    }

                    // 扫描期间让路给测试指令，只等到下一轮。
                    if (suspended)
                    {
                        continue;
                    }

                    try
                    {
                        // 设备不支持 SYS:STAT?，改用支持的 GPDADC 做只读探活（不改变光路）。
                        await ReadPdAsync(1, token).ConfigureAwait(false);
                        SetStatus(new BoardStatus { Online = true, Ready = true });
                        failures = 0;
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (SerialLinkException)
                    {
                        failures++;
                        if (failures >= HeartbeatFailureBudget)
                        {
                            await HandleHeartbeatLossAsync().ConfigureAwait(false);
                            failures = 0;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log.Error("心跳循环异常", ex);
                }
            }
        }, token);
    }

    private void StopHeartbeat()
    {
        var cts = Interlocked.Exchange(ref _heartbeatCts, null);
        cts?.Cancel();
        cts?.Dispose();
    }

    private void StartReconnectLoop()
    {
        // AutoReconnect is honoured here: a user who turned it off must not see surprise reconnects.
        if (_reconnectLoop is { IsCompleted: false } || !_userWantsConnected || !_settings.AutoReconnect)
        {
            return;
        }

        _reconnectCts = new CancellationTokenSource();
        var token = _reconnectCts.Token;

        _reconnectLoop = Task.Run(async () =>
        {
            var interval = Math.Max(500, _settings.ReconnectIntervalMs);

            while (_userWantsConnected && !token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(interval, token).ConfigureAwait(false);

                    if (!_userWantsConnected || token.IsCancellationRequested)
                    {
                        break;
                    }

                    if (_port.IsOpen)
                    {
                        SetIdentity(new BoardIdentity { Online = true, RawResponse = "串口已打开（设备无握手指令）" });
                        SetStatus(new BoardStatus { Online = true, Ready = true });
                        break;
                    }

                    _log.Info($"尝试重连 {_settings.PortName} ...");
                    await _port.OpenAsync(_settings, token).ConfigureAwait(false);

                    SetIdentity(new BoardIdentity { Online = true, RawResponse = "串口已打开（设备无握手指令）" });
                    SetStatus(new BoardStatus { Online = true, Ready = true });
                    _log.Info($"重连成功，{_settings.PortName} 已恢复。");
                    break;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SerialLinkException)
                {
                    // 重连失败累计到上限即停止，避免无限重连刷屏；用户可手动重连。
                    if (++_reconnectFailures >= 5)
                    {
                        _log.Warn("自动重连多次失败，已停止；请检查接线后手动点击连接。");
                        _userWantsConnected = false;
                        break;
                    }
                }
                catch (Exception ex)
                {
                    _log.Error("自动重连循环异常", ex);
                }
            }
        }, token);
    }

    private void SetIdentity(BoardIdentity identity)
    {
        lock (_sync)
        {
            _identity = identity;
        }

        IdentityChanged?.Invoke(this, identity);
    }

    private void SetStatus(BoardStatus status)
    {
        lock (_sync)
        {
            _status = status;
        }

        StatusChanged?.Invoke(this, status);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _userWantsConnected = false;

        _port.StateChanged -= OnPortStateChanged;
        _port.UnsolicitedLineReceived -= OnUnsolicitedLine;
        _port.FrameTraced -= OnFrameTraced;

        StopHeartbeat();

        var reconnect = Interlocked.Exchange(ref _reconnectCts, null);
        reconnect?.Cancel();
        reconnect?.Dispose();

        _port.Dispose();
    }
}
