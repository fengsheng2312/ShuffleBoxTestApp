using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShuffleBoxTestApp.Services;

namespace ShuffleBoxTestApp.ViewModels;

/// <summary>一条 PD 采集记录，显示在回读容器下方的记录表里。</summary>
public sealed record PdSample
{
    public int Index { get; init; }

    public string Time { get; init; } = string.Empty;

    /// <summary>采集时的 PD 编号。</summary>
    public string PdId { get; init; } = string.Empty;

    /// <summary>采集时的光开关位置（MEMS x / ch y）。</summary>
    public string Mems { get; init; } = string.Empty;

    /// <summary>ADC 原始值文本；采集失败时为 "—"。</summary>
    public string AdcText { get; init; } = "—";

    public string Raw { get; init; } = string.Empty;

    public bool Ok { get; init; }

    public string State => Ok ? "正常" : "失败";
}

/// <summary>
/// 设备手动控制页视图模型：光开关切换（SMSCH）+ PD 采集回读（GPDADC）。
/// 协议已确认：mems_id 1~16、mems_ch 1~64、pd_id 1~16，PD 回读为原始 ADC 值。
/// </summary>
public partial class DeviceControlViewModel : ObservableObject, IDisposable
{
    private readonly IMainBoardService _board;
    private readonly ILogService _log;
    private readonly IUiDispatcher _dispatch;

    /// <summary>采集记录保留上限，防止长时间连续采集把内存吃满。</summary>
    private const int MaxSamples = 500;

    private readonly CancellationTokenSource _cts = new();
    private System.Windows.Threading.DispatcherTimer? _monitorTimer;
    private int _sampleIndex;
    private bool _disposed;

    public DeviceControlViewModel(IMainBoardService board, ILogService log, IUiDispatcher dispatch)
    {
        _board = board;
        _log = log;
        _dispatch = dispatch;

        for (var i = MainBoardProtocol.MemsIdMin; i <= MainBoardProtocol.MemsIdMax; i++)
        {
            MemsIds.Add(i);
        }

        for (var i = MainBoardProtocol.PdIdMin; i <= MainBoardProtocol.PdIdMax; i++)
        {
            PdIds.Add(i);
        }
    }

    /// <summary>mems_id 可选值 1~16（下拉框）。</summary>
    public ObservableCollection<int> MemsIds { get; } = [];

    /// <summary>pd_id 可选值 1~16（下拉框）。</summary>
    public ObservableCollection<int> PdIds { get; } = [];

    /// <summary>mems_ch 输入范围提示。</summary>
    public string MemsChRange => $"{MainBoardProtocol.MemsChMin}~{MainBoardProtocol.MemsChMax}";

    /// <summary>PD 采集记录（最新的在最上面）。</summary>
    public ObservableCollection<PdSample> Samples { get; } = [];

    // ==================== 开关切换 ====================

    [ObservableProperty]
    private int memsId = 1;

    /// <summary>mems_ch 用输入框（1~64），避免下拉过长。以字符串接收便于校验非法输入。</summary>
    [ObservableProperty]
    private string memsChText = "1";

    /// <summary>当前已生效的光开关位置；未下发过为"未设置"。</summary>
    [ObservableProperty]
    private string appliedMems = "未设置";

    // ==================== PD 采集 ====================

    [ObservableProperty]
    private int pdId = 1;

    /// <summary>回读容器主数值（ADC 原始值）。</summary>
    [ObservableProperty]
    private string pdValue = "—";

    [ObservableProperty]
    private string pdUnit = "ADC";

    /// <summary>回读容器副标题：采集时间与通道上下文。</summary>
    [ObservableProperty]
    private string pdContext = "尚未采集";

    /// <summary>主控板应答原文，便于协议联调时核对。</summary>
    [ObservableProperty]
    private string pdRaw = "—";

    [ObservableProperty]
    private bool pdOk;

    [ObservableProperty]
    private string pdStateText = "等待采集";

    /// <summary>连续采集开关。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MonitorButtonText))]
    [NotifyPropertyChangedFor(nameof(CanToggleMonitor))]
    [NotifyCanExecuteChangedFor(nameof(ToggleMonitorCommand))]
    private bool isMonitoring;

    public string MonitorButtonText => IsMonitoring ? "■ 停止连续采集" : "▶ 连续采集";

    /// <summary>连续采集间隔（毫秒）。</summary>
    [ObservableProperty]
    private int monitorIntervalMs = 500;

    // ==================== 可用性 ====================

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOperate))]
    [NotifyPropertyChangedFor(nameof(CanToggleMonitor))]
    [NotifyCanExecuteChangedFor(nameof(SwitchCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReadPdCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleMonitorCommand))]
    private bool isAvailable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOperate))]
    [NotifyPropertyChangedFor(nameof(CanToggleMonitor))]
    [NotifyCanExecuteChangedFor(nameof(SwitchCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReadPdCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleMonitorCommand))]
    private bool isBusy;

    /// <summary>已连接、未被扫描占用、且当前没有指令在途。</summary>
    public bool CanOperate => IsAvailable && !IsBusy;

    /// <summary>连续采集按钮的可用性：采集中必须仍可点击，否则用户无法停止。</summary>
    public bool CanToggleMonitor => IsMonitoring ? IsAvailable : CanOperate;

    /// <summary>由主视图模型在链路/扫描状态变化时调用。</summary>
    public void RefreshAvailability(bool linkOpen, bool scanning)
    {
        IsAvailable = linkOpen && !scanning;

        if (!IsAvailable && IsMonitoring)
        {
            StopMonitoring();
        }
    }

    // ==================== 命令 ====================

    /// <summary>下发 SMSCH mems_id mems_ch。</summary>
    [RelayCommand(CanExecute = nameof(CanOperate))]
    private async Task SwitchAsync()
    {
        if (!int.TryParse(MemsChText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ch)
            || ch < MainBoardProtocol.MemsChMin || ch > MainBoardProtocol.MemsChMax)
        {
            _log.Warn($"mems_ch 需为 {MemsChRange} 的整数，当前输入：{MemsChText}");
            return;
        }

        IsBusy = true;
        try
        {
            await _board.SwitchMemsAsync(MemsId, ch, _cts.Token);
            AppliedMems = $"MEMS {MemsId} / ch {ch}";
            _log.Info($"光开关已切换：SMSCH {MemsId} {ch}");
        }
        catch (OperationCanceledException)
        {
            // 关窗取消，属正常。
        }
        catch (Exception ex)
        {
            _log.Error("光开关切换失败", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>点一次采集：GPDADC pd_id，把 ADC 回读值填进容器与记录表。</summary>
    [RelayCommand(CanExecute = nameof(CanOperate))]
    private async Task ReadPdAsync()
    {
        IsBusy = true;
        try
        {
            await SampleOnceAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>连续采集：按设定间隔轮询，用于观察 ADC 漂移。</summary>
    [RelayCommand(CanExecute = nameof(CanToggleMonitor))]
    private void ToggleMonitor()
    {
        if (IsMonitoring)
        {
            StopMonitoring();
            return;
        }

        var interval = Math.Clamp(MonitorIntervalMs, 100, 10000);
        MonitorIntervalMs = interval;

        _monitorTimer ??= CreateTimer();
        _monitorTimer.Interval = TimeSpan.FromMilliseconds(interval);
        _monitorTimer.Start();

        IsMonitoring = true;
        _log.Info($"开始连续采集 PD{PdId}，间隔 {interval} ms");
    }

    private System.Windows.Threading.DispatcherTimer CreateTimer()
    {
        var timer = new System.Windows.Threading.DispatcherTimer();
        timer.Tick += async (_, _) =>
        {
            // 上一条还没回来就跳过这一拍，避免指令堆积把串口塞满。
            if (IsBusy || !CanOperate)
            {
                return;
            }

            IsBusy = true;
            try
            {
                await SampleOnceAsync();
            }
            finally
            {
                IsBusy = false;
            }
        };
        return timer;
    }

    /// <summary>停止连续采集（切页、断线、扫描开始前都会调用）。</summary>
    public void StopMonitoring()
    {
        if (_monitorTimer is not null)
        {
            _monitorTimer.Stop();
        }

        if (IsMonitoring)
        {
            IsMonitoring = false;
            _log.Info("已停止连续采集");
        }
    }

    private async Task SampleOnceAsync()
    {
        var time = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        var mems = AppliedMems;
        var id = PdId;

        try
        {
            var adc = await _board.ReadPdAsync(id, _cts.Token);
            var text = adc.ToString(CultureInfo.InvariantCulture);

            await _dispatch.InvokeAsync(() =>
            {
                PdValue = text;
                PdOk = true;
                PdStateText = "采集成功";
                PdRaw = $"PD_{id:D2} ADC: {text};";
                PdContext = $"{time}　PD {id}　{mems}";
                AddSample(new PdSample
                {
                    Index = ++_sampleIndex,
                    Time = time,
                    PdId = $"PD {id}",
                    Mems = mems,
                    AdcText = text,
                    Raw = $"PD_{id:D2} ADC: {text};",
                    Ok = true,
                });
            });
        }
        catch (OperationCanceledException)
        {
            // 忽略。
        }
        catch (Exception ex)
        {
            await _dispatch.InvokeAsync(() =>
            {
                PdOk = false;
                PdStateText = "采集失败";
                PdValue = "—";
                PdRaw = ex.Message;
                PdContext = $"{time}　PD {id}　{mems}";
                AddSample(new PdSample
                {
                    Index = ++_sampleIndex,
                    Time = time,
                    PdId = $"PD {id}",
                    Mems = mems,
                    Ok = false,
                    Raw = ex.Message,
                });
            });

            _log.Warn($"PD 采集失败：{ex.Message}");

            // 连续采集中遇到链路问题就停下来，避免刷屏式报错。
            if (IsMonitoring)
            {
                StopMonitoring();
            }
        }
    }

    private void AddSample(PdSample sample)
    {
        Samples.Insert(0, sample);
        while (Samples.Count > MaxSamples)
        {
            Samples.RemoveAt(Samples.Count - 1);
        }
    }

    [RelayCommand]
    private void ClearSamples()
    {
        Samples.Clear();
        _sampleIndex = 0;
        PdValue = "—";
        PdRaw = "—";
        PdContext = "尚未采集";
        PdStateText = "等待采集";
        PdOk = false;
        _log.Info("已清空 PD 采集记录");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopMonitoring();
        _cts.Cancel();
        _cts.Dispose();
    }
}
