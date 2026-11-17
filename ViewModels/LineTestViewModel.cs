using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShuffleBoxTestApp.Models;
using ShuffleBoxTestApp.Services;

namespace ShuffleBoxTestApp.ViewModels;

/// <summary>
/// 线序测试页（LineTestView.xaml）的专属视图模型，严格 MVVM：
/// 页面所有绑定属性与命令都在此，View 不含业务逻辑。
/// 依赖业务层接口（引擎/报告/日志），不直接做串口 I/O。
/// 连接状态由 MainViewModel 推送（SetLinkOpen），扫描状态经 IsScanningChanged 供主窗口联动。
/// </summary>
public partial class LineTestViewModel : ObservableObject, IDisposable
{
    private readonly ITestEngine _engine;
    private readonly ITestReportService _report;
    private readonly ILogService _log;
    private readonly IUiDispatcher _dispatch;

    /// <summary>测试配方（阈值/模式/报告目录），与引擎共享同一实例。</summary>
    private readonly ProductRecipe _recipe;

    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    /// <summary>扫描状态变化，供主窗口联动（禁用手动手页/断开时停扫描）。</summary>
    public event Action<bool>? IsScanningChanged;

    /// <summary>请求停止设备手动页的连续采集（扫描开始前调用，避免抢占串口）。</summary>
    public event Action? StopDeviceMonitoringRequested;

    public LineTestViewModel(
        ITestEngine engine,
        ITestReportService report,
        ILogService log,
        IUiDispatcher dispatch,
        ProductRecipe recipe)
    {
        _engine = engine;
        _report = report;
        _log = log;
        _dispatch = dispatch;
        _recipe = recipe;

        _engine.ResultProduced += OnResultProduced;
        _engine.ProgressChanged += OnProgress;
        _engine.Completed += OnScanCompleted;
    }

    // ==================== 绑定属性 ====================

    /// <summary>测试结果行（异常整行标红）。</summary>
    public ObservableCollection<ChannelTestResult> Results { get; } = [];

    /// <summary>ADC 判定阈值（大于即找到）。</summary>
    public int ThresholdAdc
    {
        get => _recipe.PdThresholdAdc;
        set
        {
            if (_recipe.PdThresholdAdc == value) return;
            _recipe.PdThresholdAdc = value;
            OnPropertyChanged();
        }
    }

    /// <summary>进光范围表达式：如 "1,3,4"、"1~50"、"1-50"，可混合；留空=全部。</summary>
    [ObservableProperty]
    private string inputRangeText = string.Empty;

    /// <summary>测试模式。</summary>
    public TestMode Mode
    {
        get => _recipe.Mode;
        set
        {
            if (_recipe.Mode == value) return;
            _recipe.Mode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsKnownMode));
        }
    }

    public bool IsKnownMode => Mode == TestMode.KnownTraversal;

    /// <summary>报告输出目录。</summary>
    public string ReportDir
    {
        get => _recipe.ReportDirectory;
        set
        {
            if (_recipe.ReportDirectory == value) return;
            _recipe.ReportDirectory = value;
            OnPropertyChanged();
        }
    }

    [ObservableProperty]
    private string knownReportPath = string.Empty;

    [ObservableProperty]
    private string lastReportPath = string.Empty;

    [ObservableProperty]
    private int completedCount;

    [ObservableProperty]
    private int totalCount;

    [ObservableProperty]
    private int passedCount;

    [ObservableProperty]
    private double progressValue;

    [ObservableProperty]
    private string progressText = "未开始";

    /// <summary>链路是否打开（由主窗口推送）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanScan))]
    private bool isLinkOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanScan))]
    private bool isScanning;

    public bool CanScan => IsLinkOpen && !IsScanning;

    partial void OnIsScanningChanged(bool value) => IsScanningChanged?.Invoke(value);

    /// <summary>主窗口在链路状态变化时推送。</summary>
    public void SetLinkOpen(bool open) => IsLinkOpen = open;

    // ==================== 命令 ====================

    /// <summary>导入旧版测试报告（已知遍历）。</summary>
    [RelayCommand]
    private async Task UploadKnownAsync()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Excel 报告 (*.xlsx)|*.xlsx",
            Title = "选择旧版测试报告",
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var map = await _report.LoadKnownAsync(dlg.FileName, _cts.Token);
            _engine.KnownMap = map;
            KnownReportPath = dlg.FileName;
            Mode = TestMode.KnownTraversal;
            _log.Info($"已加载旧报告 {System.IO.Path.GetFileName(dlg.FileName)}，共 {map.Count} 条已知映射");
        }
        catch (Exception ex)
        {
            _log.Error("读取旧报告失败", ex);
        }
    }

    [RelayCommand]
    private async Task StartScanAsync()
    {
        if (!IsLinkOpen)
        {
            _log.Warn("请先连接主控板");
            return;
        }

        if (!ChannelRangeParser.TryParse(InputRangeText, _recipe.ChannelCount, out var channels, out var err))
        {
            _log.Error($"进光范围无效：{err}");
            return;
        }

        // 扫描开始前先停手动连续采集，避免抢占串口。
        StopDeviceMonitoringRequested?.Invoke();

        await _dispatch.InvokeAsync(() =>
        {
            Results.Clear();
            CompletedCount = 0;
            PassedCount = 0;
            TotalCount = channels.Count;
            ProgressValue = 0;
            ProgressText = "扫描中 ...";
            IsScanning = true;
        });

        await _engine.StartAsync(channels, _cts.Token);
    }

    [RelayCommand]
    private Task PauseScanAsync() => IsScanning ? _engine.PauseAsync() : Task.CompletedTask;

    [RelayCommand]
    private Task ResumeScanAsync() => _engine.ResumeAsync();

    [RelayCommand]
    private Task StopScanAsync() => _engine.StopAsync();

    // ==================== 引擎事件（转派界面线程） ====================

    private void OnResultProduced(object? sender, ChannelTestResult result) => _dispatch.Post(() =>
    {
        Results.Add(result);
    });

    private void OnProgress(object? sender, ScanProgress progress) => _dispatch.Post(() =>
    {
        CompletedCount = progress.Completed;
        PassedCount = progress.Passed;
        TotalCount = progress.TotalChannels;
        ProgressValue = progress.Ratio;
        ProgressText = $"第 {progress.CurrentChannel}/{progress.TotalChannels} 路　" +
                       $"找到 {progress.Passed}/{Math.Max(1, progress.Completed)}　" +
                       $"已用 {FormatSpan(progress.Elapsed)}　剩余 {FormatSpan(progress.Eta)}";
    });

    private void OnScanCompleted(object? sender, ScanCompletedEventArgs e) => _dispatch.Post(async () =>
    {
        IsScanning = false;
        ProgressValue = e.Summary.Stopped ? ProgressValue : 1;
        ProgressText = $"{e.Summary.Conclusion}　找到 {e.Summary.Found}/{e.Summary.Total}　标红 {e.Summary.Red}";

        // 每次测试生成单独的时间命名报告；已知模式保留旧表写 E/F。
        try
        {
            var snapshot = Results.ToList();
            var path = await _report.SaveAsync(snapshot, ReportDir, Mode, KnownReportPath, _cts.Token);
            LastReportPath = path;
            _log.Info($"测试报告已生成：{path}");
        }
        catch (Exception ex)
        {
            _log.Error("生成测试报告失败", ex);
        }
    });

    private static string FormatSpan(TimeSpan span) =>
        span >= TimeSpan.FromHours(1) ? span.ToString(@"h\:mm\:ss") : span.ToString(@"mm\:ss");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _engine.ResultProduced -= OnResultProduced;
        _engine.ProgressChanged -= OnProgress;
        _engine.Completed -= OnScanCompleted;

        _cts.Cancel();
        _cts.Dispose();
    }
}
