using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShuffleBoxTestApp.Models;
using ShuffleBoxTestApp.Services;

namespace ShuffleBoxTestApp.ViewModels;

/// <summary>中央工作区可切换的子页面。</summary>
public enum AppPage
{
    /// <summary>线序全量测试页（默认）。</summary>
    LineTest,

    /// <summary>设备手动控制页。</summary>
    DeviceControl,
}

/// <summary>
/// 主窗口视图模型。只负责全局共享部分：串口连接/状态、页面导航、运行日志、SN 绑定。
/// 两个子页面各有专属视图模型（LineTest / DeviceControl），严格一页一 VM。
/// 主窗口负责子 VM 之间的状态联动（链路→子页、扫描→导航/手动页）。
/// </summary>
public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IMainBoardService _board;
    private readonly IPortInfoProvider _portInfo;
    private readonly ILogService _log;
    private readonly IUiDispatcher _dispatch;

    /// <summary>线序测试页视图模型。</summary>
    public LineTestViewModel LineTest { get; }

    /// <summary>设备手动控制页视图模型。</summary>
    public DeviceControlViewModel DeviceControl { get; }

    /// <summary>日志区显示上限，超出后丢弃最早的行，保证 7×24 运行内存不增长。</summary>
    private const int MaxLogLines = 800;

    private readonly List<LogLine> _logBuffer = [];
    private readonly CancellationTokenSource _cts = new();
    private bool _logFlushScheduled;
    private bool _disposed;

    public MainViewModel(
        IMainBoardService board,
        IPortInfoProvider portInfo,
        ILogService log,
        IUiDispatcher dispatch,
        DeviceControlViewModel deviceControl,
        LineTestViewModel lineTest)
    {
        _board = board;
        _portInfo = portInfo;
        _log = log;
        _dispatch = dispatch;
        DeviceControl = deviceControl;
        LineTest = lineTest;

        _board.StateChanged += OnBoardStateChanged;
        _board.IdentityChanged += OnIdentityChanged;
        _board.StatusChanged += OnStatusChanged;
        _board.UnsolicitedLine += OnUnsolicitedLine;
        _log.LineAdded += OnLogLine;

        // 子 VM 联动：扫描状态→导航/手动页；扫描前→停手动采集。
        LineTest.IsScanningChanged += OnChildScanningChanged;
        LineTest.StopDeviceMonitoringRequested += DeviceControl.StopMonitoring;

        RefreshPortsSafe();
    }

    // ==================== 串口 / 全局状态 ====================

    public ObservableCollection<SerialPortInfo> Ports { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConnect))]
    private SerialPortInfo? selectedPort;

    public int[] BaudRates { get; } = [9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600];

    [ObservableProperty]
    private int selectedBaudRate = 115200;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRefreshPorts))]
    private bool isRefreshingPorts;

    public bool CanRefreshPorts => !IsRefreshingPorts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConnect))]
    [NotifyPropertyChangedFor(nameof(CanDisconnect))]
    private bool isConnecting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConnect))]
    [NotifyPropertyChangedFor(nameof(CanDisconnect))]
    [NotifyPropertyChangedFor(nameof(ConnectionText))]
    [NotifyPropertyChangedFor(nameof(ConnectionBrushKey))]
    private bool isLinkOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectionText))]
    [NotifyPropertyChangedFor(nameof(ConnectionBrushKey))]
    private bool isHandshakeOk;

    public bool CanConnect => !IsConnecting && !IsLinkOpen && SelectedPort is not null;

    public bool CanDisconnect => !IsConnecting && IsLinkOpen;

    public string ConnectionText
    {
        get
        {
            if (IsConnecting) return "处理中";
            if (!IsLinkOpen) return "未连接";
            return IsHandshakeOk ? "已连接" : "已连接·未握手";
        }
    }

    public string ConnectionBrushKey =>
        IsLinkOpen && IsHandshakeOk ? "StatusOkBrush"
        : IsLinkOpen ? "StatusWarnBrush"
        : "StatusOffBrush";

    [ObservableProperty]
    private string boardDisplay = "未握手";

    [ObservableProperty]
    private string heartbeatDisplay = "—";

    [ObservableProperty]
    private string currentRecipe = "SB-256-RevA";

    [ObservableProperty]
    private string scannedSerialNumber = string.Empty;

    [ObservableProperty]
    private string snInput = string.Empty;

    public ObservableCollection<LogLine> LogLines { get; } = [];

    // ==================== 页面导航 ====================

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTestPage))]
    [NotifyPropertyChangedFor(nameof(IsManualPage))]
    private AppPage currentPage = AppPage.LineTest;

    public bool IsTestPage => CurrentPage == AppPage.LineTest;

    public bool IsManualPage => CurrentPage == AppPage.DeviceControl;

    /// <summary>扫描进行中不允许进手动页，避免抢占串口。</summary>
    public bool CanEnterManual => !LineTest.IsScanning;

    [RelayCommand(CanExecute = nameof(CanEnterManual))]
    private void ShowDeviceControl()
    {
        CurrentPage = AppPage.DeviceControl;
        DeviceControl.RefreshAvailability(IsLinkOpen, LineTest.IsScanning);
    }

    [RelayCommand]
    private void ShowLineTest()
    {
        DeviceControl.StopMonitoring();
        CurrentPage = AppPage.LineTest;
    }

    // ==================== 命令 ====================

    [RelayCommand(CanExecute = nameof(CanRefreshPorts))]
    private async Task RefreshPortsAsync()
    {
        IsRefreshingPorts = true;
        try
        {
            WmiPortInfoProvider.InvalidateCache();
            var ports = await _portInfo.GetPortsAsync(_cts.Token);

            await _dispatch.InvokeAsync(() =>
            {
                var keep = SelectedPort?.PortName;
                Ports.Clear();
                foreach (var port in ports) Ports.Add(port);

                SelectedPort = keep is null
                    ? Ports.FirstOrDefault(p => p.IsHealthy) ?? Ports.FirstOrDefault()
                    : Ports.FirstOrDefault(p => p.PortName.Equals(keep, StringComparison.OrdinalIgnoreCase))
                      ?? Ports.FirstOrDefault(p => p.IsHealthy) ?? Ports.FirstOrDefault();

                _log.Info($"扫描到 {Ports.Count} 个串口" + (Ports.Count == 0 ? "（未检测到设备，检查驱动或 USB 连接）" : "：")
                          + string.Join("；", Ports.Select(p => $"{p.PortName} [{(p.HasDriverInfo ? p.DriverService : "无驱动信息")}]")));
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.Error("扫描串口失败", ex); }
        finally { IsRefreshingPorts = false; }
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        var port = SelectedPort;
        if (port is null) { _log.Warn("请先选择串口号"); return; }

        if (!port.IsHealthy)
            _log.Warn($"{port.PortName} 设备状态异常（{(string.IsNullOrWhiteSpace(port.Status) ? "未知" : port.Status)}），仍尝试连接");

        IsConnecting = true;
        try
        {
            var settings = new SerialSettings { PortName = port.PortName, BaudRate = SelectedBaudRate };
            _log.Info($"正在连接 {settings} ...");
            await _board.ConnectAsync(settings, _cts.Token);
            await _dispatch.InvokeAsync(() => IsLinkOpen = _board.State == LinkState.Open);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.Error($"连接 {port.PortName} 失败", ex);
            await _dispatch.InvokeAsync(() => IsLinkOpen = false);
        }
        finally
        {
            IsConnecting = false;
            NotifyGlobalStates();
        }
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        IsConnecting = true;
        try
        {
            if (LineTest.IsScanning) await LineTest.StopScanCommand.ExecuteAsync(null);
            await _board.DisconnectAsync();
            _log.Info("已主动断开连接（不会自动重连）");
        }
        catch (Exception ex) { _log.Error("断开时出错", ex); }
        finally
        {
            IsConnecting = false;
            NotifyGlobalStates();
        }
    }

    /// <summary>扫码枪回车提交 SN（F-SPC-01）。</summary>
    [RelayCommand]
    private void CommitSerialNumber()
    {
        var sn = SnInput.Trim();
        if (sn.Length == 0) return;
        ScannedSerialNumber = sn;
        SnInput = string.Empty;
        _log.Info($"已绑定被测件 SN：{sn}");
    }

    // ==================== 设备层事件 ====================

    private void OnBoardStateChanged(object? sender, LinkState state) => _dispatch.Post(() =>
    {
        IsLinkOpen = state == LinkState.Open;
        if (state == LinkState.Faulted)
        {
            IsHandshakeOk = false;
            _log.Warn("链路异常，正在自动重连 ...");
        }
        NotifyGlobalStates();
    });

    private void OnIdentityChanged(object? sender, BoardIdentity identity) => _dispatch.Post(() =>
    {
        IsHandshakeOk = identity.Online;
        BoardDisplay = identity.Display;
    });

    private void OnStatusChanged(object? sender, BoardStatus status) => _dispatch.Post(() =>
        HeartbeatDisplay = status.Display);

    private void OnUnsolicitedLine(object? sender, string line) =>
        _log.Info($"主控板上报：{line}");

    /// <summary>子页扫描状态变化→刷新导航/手动页可用性。</summary>
    private void OnChildScanningChanged(bool scanning) => _dispatch.Post(() =>
    {
        DeviceControl.RefreshAvailability(IsLinkOpen, scanning);
        ShowDeviceControlCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanEnterManual));
    });

    private void OnLogLine(object? sender, LogLine line)
    {
        lock (_logBuffer) { _logBuffer.Add(line); }
        if (_logFlushScheduled) return;
        _logFlushScheduled = true;
        _dispatch.Post(FlushLogBuffer);
    }

    private void FlushLogBuffer()
    {
        LogLine[] batch;
        lock (_logBuffer)
        {
            batch = [.. _logBuffer];
            _logBuffer.Clear();
            _logFlushScheduled = false;
        }
        if (batch.Length == 0) return;

        foreach (var line in batch) LogLines.Add(line);
        while (LogLines.Count > MaxLogLines) LogLines.RemoveAt(0);
    }

    // ==================== 辅助 ====================

    private void NotifyGlobalStates()
    {
        // 链路状态推送给两个子页。
        LineTest.SetLinkOpen(IsLinkOpen);
        DeviceControl.RefreshAvailability(IsLinkOpen, LineTest.IsScanning);
        ShowDeviceControlCommand.NotifyCanExecuteChanged();

        OnPropertyChanged(nameof(CanConnect));
        OnPropertyChanged(nameof(CanDisconnect));
        OnPropertyChanged(nameof(ConnectionText));
        OnPropertyChanged(nameof(ConnectionBrushKey));
        OnPropertyChanged(nameof(CanEnterManual));
    }

    public SerialSettings BuildSettings() => new()
    {
        PortName = SelectedPort?.PortName ?? string.Empty,
        BaudRate = SelectedBaudRate,
    };

    private void RefreshPortsSafe()
    {
        try { RefreshPortsCommand.Execute(null); }
        catch (Exception ex) { _log.Error("初始化串口列表失败", ex); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        LineTest.IsScanningChanged -= OnChildScanningChanged;
        LineTest.StopDeviceMonitoringRequested -= DeviceControl.StopMonitoring;
        LineTest.Dispose();
        DeviceControl.Dispose();

        _board.StateChanged -= OnBoardStateChanged;
        _board.IdentityChanged -= OnIdentityChanged;
        _board.StatusChanged -= OnStatusChanged;
        _board.UnsolicitedLine -= OnUnsolicitedLine;
        _log.LineAdded -= OnLogLine;

        _cts.Cancel();
        _cts.Dispose();
    }
}
