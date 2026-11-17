using ShuffleBoxTestApp.Models;

namespace ShuffleBoxTestApp.Services;

public sealed record ScanProgress
{
    public int CurrentChannel { get; init; }
    public int TotalChannels { get; init; }
    public int Completed { get; init; }
    public int Passed { get; init; }
    public TimeSpan Elapsed { get; init; }

    public TimeSpan Eta
    {
        get
        {
            if (Completed <= 0 || Completed >= TotalChannels) return TimeSpan.Zero;
            var average = Elapsed.TotalMilliseconds / Completed;
            return TimeSpan.FromMilliseconds(average * (TotalChannels - Completed));
        }
    }

    public double Ratio => TotalChannels <= 0 ? 0 : Math.Clamp((double)Completed / TotalChannels, 0, 1);
}

public sealed record ScanSummary
{
    public int Total { get; init; }
    public int Found { get; init; }
    public int NotFound { get; init; }
    public int Red { get; init; }
    public bool Stopped { get; init; }
    public int CommunicationErrors { get; init; }

    public string Conclusion => Stopped ? "中止（结论无效）"
        : NotFound == 0 && Red == 0 && Total > 0 ? "整批 PASS" : "整批 FAIL";
}

public sealed class ScanCompletedEventArgs : EventArgs
{
    public required ScanSummary Summary { get; init; }
}

public enum ScanState { Idle, Running, Paused }

/// <summary>
/// 线序测试引擎。进光 1~256（MEMS 1-1~4-64）× 出光（另一组 MEMS）遍历，
/// 切换出光后读一次数，读数大于阈值即认定找到。
/// 默认遍历用 HashMap 查出光重复标红；已知遍历先试预期出光，不达标再全遍历。
/// </summary>
public interface ITestEngine
{
    event EventHandler<ChannelTestResult>? ResultProduced;
    event EventHandler<ScanProgress>? ProgressChanged;
    event EventHandler<ScanCompletedEventArgs>? Completed;
    event EventHandler<LogLevel>? Notice;

    ScanState State { get; }

    /// <summary>已知遍历的旧报告映射（进光通道 → 输出/ADC），由界面上传后注入。</summary>
    IReadOnlyDictionary<int, (int Output, int Adc)>? KnownMap { get; set; }

    /// <summary>按指定进光通道列表测试；null 表示全部 1~ChannelCount。</summary>
    Task StartAsync(IReadOnlyList<int>? channels = null, CancellationToken cancellationToken = default);
    Task PauseAsync();
    Task ResumeAsync();
    Task StopAsync();
}

public sealed class TestEngine : ITestEngine
{
    private readonly IMainBoardService _board;
    private readonly ILogService _log;
    private readonly Func<ProductRecipe> _recipeProvider;

    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private readonly ManualResetEventSlim _resumeGate = new(true);

    private CancellationTokenSource? _scanCts;
    private volatile ScanState _state = ScanState.Idle;
    private volatile bool _stopRequested;

    public TestEngine(IMainBoardService board, ILogService log, Func<ProductRecipe> recipeProvider)
    {
        _board = board;
        _log = log;
        _recipeProvider = recipeProvider;
    }

    public event EventHandler<ChannelTestResult>? ResultProduced;
    public event EventHandler<ScanProgress>? ProgressChanged;
    public event EventHandler<ScanCompletedEventArgs>? Completed;
    public event EventHandler<LogLevel>? Notice;

    public ScanState State => _state;
    public IReadOnlyDictionary<int, (int Output, int Adc)>? KnownMap { get; set; }

    public async Task StartAsync(IReadOnlyList<int>? channels = null, CancellationToken cancellationToken = default)
    {
        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state == ScanState.Running)
            {
                _log.Warn("测试已在进行中，请先停止当前扫描");
                Notice.Raise(this, LogLevel.Warn);
                return;
            }

            if (_board.State != LinkState.Open)
            {
                _log.Error("主控板未连接，无法开始测试");
                Notice.Raise(this, LogLevel.Error);
                return;
            }

            var recipe = _recipeProvider();
            if (recipe.Mode == TestMode.KnownTraversal && (KnownMap is null || KnownMap.Count == 0))
            {
                _log.Error("已知遍历需先导入正确的旧版测试报告(.xlsx)");
                Notice.Raise(this, LogLevel.Error);
                return;
            }

            _board.SetHeartbeatSuspended(true);
            _scanCts?.Cancel();
            _scanCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _stopRequested = false;
            _resumeGate.Set();
            _state = ScanState.Running;

            // 指定进光列表：去重升序并夹取到有效范围；null 表示全部。
            var list = channels is null
                ? Enumerable.Range(1, recipe.ChannelCount).ToList()
                : channels.Where(c => c >= 1 && c <= recipe.ChannelCount).Distinct().OrderBy(c => c).ToList();

            if (list.Count == 0)
            {
                _log.Error("进光范围为空，无可测试通道");
                Notice.Raise(this, LogLevel.Error);
                _state = ScanState.Idle;
                _board.SetHeartbeatSuspended(false);
                return;
            }

            var token = _scanCts.Token;
            _ = Task.Run(() => RunAsync(recipe, list, token), CancellationToken.None);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public Task PauseAsync()
    {
        if (_state == ScanState.Running)
        {
            _resumeGate.Reset();
            _state = ScanState.Paused;
            _log.Info("测试已暂停");
            Notice.Raise(this, LogLevel.Info);
        }
        return Task.CompletedTask;
    }

    public Task ResumeAsync()
    {
        if (_state == ScanState.Paused)
        {
            _state = ScanState.Running;
            _resumeGate.Set();
            _log.Info("测试继续");
            Notice.Raise(this, LogLevel.Info);
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _stopRequested = true;
        _resumeGate.Set();
        var cts = _scanCts;
        if (cts is not null) await cts.CancelAsync().ConfigureAwait(false);
    }

    private async Task RunAsync(ProductRecipe recipe, IReadOnlyList<int> inputs, CancellationToken token)
    {
        var total = inputs.Count;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var results = new List<ChannelTestResult>();
        // HashMap：出光通道 → 首个进光通道，用于默认遍历重复检测。
        var outputFirstSeen = new Dictionary<int, int>();
        var commErrors = 0;
        var consecutiveTimeouts = 0;
        const int CircuitBreakerThreshold = 5;

        _log.Info($"开始扫描：{total} 路进光（{inputs[0]}~{inputs[^1]}），模式 {(recipe.Mode == TestMode.KnownTraversal ? "已知遍历" : "默认遍历")}，阈值 ADC>{recipe.PdThresholdAdc}");
        Notice.Raise(this, LogLevel.Info);

        try
        {
            foreach (var input in inputs)
            {
                if (token.IsCancellationRequested) break;
                _resumeGate.Wait(token);

                var result = recipe.Mode == TestMode.KnownTraversal
                    ? await TestKnownAsync(recipe, input, token).ConfigureAwait(false)
                    : await TestDefaultAsync(recipe, input, outputFirstSeen, token).ConfigureAwait(false);

                if (result is null)
                {
                    commErrors++;
                    consecutiveTimeouts++;
                    if (consecutiveTimeouts >= CircuitBreakerThreshold)
                    {
                        _log.Error($"连续 {consecutiveTimeouts} 路通信失败，自动暂停扫描（第 {input} 路）");
                        Notice.Raise(this, LogLevel.Error);
                        _stopRequested = true;
                        break;
                    }
                    continue;
                }

                consecutiveTimeouts = 0;
                results.Add(result);
                ResultProduced.Raise(this, result);
                ProgressChanged.Raise(this, new ScanProgress
                {
                    CurrentChannel = input,
                    TotalChannels = total,
                    Completed = results.Count,
                    Passed = results.Count(x => x.Found),
                    Elapsed = stopwatch.Elapsed,
                });
            }
        }
        catch (OperationCanceledException) { _stopRequested = true; }
        catch (Exception ex)
        {
            _log.Error($"扫描循环异常终止：{ex.Message}");
            Notice.Raise(this, LogLevel.Error);
            _stopRequested = true;
        }
        finally
        {
            stopwatch.Stop();
            _board.SetHeartbeatSuspended(false);
            _state = ScanState.Idle;
            _resumeGate.Set();

            var summary = new ScanSummary
            {
                Total = results.Count,
                Found = results.Count(x => x.Found),
                NotFound = results.Count(x => !x.Found),
                Red = results.Count(x => x.IsRed),
                CommunicationErrors = commErrors,
                Stopped = _stopRequested || token.IsCancellationRequested,
            };

            var level = summary.Conclusion.EndsWith("PASS") ? LogLevel.Info : LogLevel.Warn;
            _log.Log(level, $"扫描结束：{summary.Conclusion}，找到 {summary.Found}/{summary.Total}，标红 {summary.Red}");
            Notice.Raise(this, level);
            Completed.Raise(this, new ScanCompletedEventArgs { Summary = summary });
        }
    }

    /// <summary>默认遍历：切进光 → 逐出光切换并读数，首个大于阈值者记录；HashMap 查重标红。</summary>
    private async Task<ChannelTestResult?> TestDefaultAsync(
        ProductRecipe recipe, int input, Dictionary<int, int> outputFirstSeen, CancellationToken token)
    {
        try
        {
            await SwitchInputAsync(recipe, input, token).ConfigureAwait(false);

            int foundOutput = 0, foundAdc = 0;
            for (var output = 1; output <= recipe.OutputCount; output++)
            {
                token.ThrowIfCancellationRequested();
                var adc = await SwitchOutputAndReadAsync(recipe, output, token).ConfigureAwait(false);
                if (adc > recipe.PdThresholdAdc)
                {
                    foundOutput = output;
                    foundAdc = adc;
                    break;
                }
            }

            var result = new ChannelTestResult
            {
                InputChannel = input,
                SwitchLabel = InputLabel(input),
                OutputChannel = foundOutput,
                Adc = foundAdc,
                Remark = foundOutput > 0 ? "实测" : "未找到",
            };

            if (foundOutput > 0)
            {
                if (outputFirstSeen.TryGetValue(foundOutput, out var first))
                {
                    result.IsRed = true;
                    result.Remark = $"重复(与{first}同出光{foundOutput})";
                }
                else
                {
                    outputFirstSeen[foundOutput] = input;
                }
            }

            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (SerialTimeoutException ex) { _log.Warn($"第 {input} 路超时跳过：{ex.Message}"); Notice.Raise(this, LogLevel.Warn); return null; }
        catch (SerialLinkException ex) { _log.Error($"第 {input} 路通信失败：{ex.Message}"); Notice.Raise(this, LogLevel.Error); return null; }
    }

    /// <summary>已知遍历：先切预期出光读数，达标直接记录；不达标全遍历，记录已知ADC与实际值，不一致标红。</summary>
    private async Task<ChannelTestResult?> TestKnownAsync(ProductRecipe recipe, int input, CancellationToken token)
    {
        try
        {
            var expected = KnownMap!.TryGetValue(input, out var k) ? k.Output : 0;
            var oldAdc = KnownMap.TryGetValue(input, out var k2) ? k2.Adc : 0;

            await SwitchInputAsync(recipe, input, token).ConfigureAwait(false);

            // 先试预期出光。
            var knownAdc = 0;
            if (expected > 0)
            {
                knownAdc = await SwitchOutputAndReadAsync(recipe, expected, token).ConfigureAwait(false);
            }

            int finalOutput, finalAdc;
            bool metDirect;
            if (expected > 0 && knownAdc > recipe.PdThresholdAdc)
            {
                finalOutput = expected;
                finalAdc = knownAdc;
                metDirect = true;
            }
            else
            {
                // 不达标：全遍历找最终结果。
                finalOutput = 0; finalAdc = 0; metDirect = false;
                for (var output = 1; output <= recipe.OutputCount; output++)
                {
                    token.ThrowIfCancellationRequested();
                    var adc = await SwitchOutputAndReadAsync(recipe, output, token).ConfigureAwait(false);
                    if (adc > recipe.PdThresholdAdc)
                    {
                        finalOutput = output;
                        finalAdc = adc;
                        break;
                    }
                }
            }

            var mismatch = expected > 0 && finalOutput != expected;
            return new ChannelTestResult
            {
                InputChannel = input,
                SwitchLabel = InputLabel(input),
                OutputChannel = finalOutput,
                Adc = finalAdc,
                ExpectedOutput = expected,
                KnownAdc = knownAdc,
                MetDirect = metDirect,
                IsRed = mismatch,
                Remark = mismatch ? $"不一致(旧{expected}→新{finalOutput})"
                         : metDirect ? "已知命中"
                         : finalOutput > 0 ? "遍历找到" : "未找到",
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (SerialTimeoutException ex) { _log.Warn($"第 {input} 路超时跳过：{ex.Message}"); Notice.Raise(this, LogLevel.Warn); return null; }
        catch (SerialLinkException ex) { _log.Error($"第 {input} 路通信失败：{ex.Message}"); Notice.Raise(this, LogLevel.Error); return null; }
    }

    private async Task SwitchInputAsync(ProductRecipe recipe, int input, CancellationToken token)
    {
        var (m, c) = MapInput(input);
        await _board.SwitchMemsAsync(m, c, token).ConfigureAwait(false);
        await Task.Delay(recipe.SwitchSettleMs, token).ConfigureAwait(false);
    }

    private async Task<int> SwitchOutputAndReadAsync(ProductRecipe recipe, int output, CancellationToken token)
    {
        var (m, c) = MapOutput(recipe, output);
        await _board.SwitchMemsAsync(m, c, token).ConfigureAwait(false);
        await Task.Delay(recipe.SwitchSettleMs, token).ConfigureAwait(false);
        return await _board.ReadPdAsync(1, token).ConfigureAwait(false);
    }

    /// <summary>进光通道 → (mems 1-4, ch 1-64)。</summary>
    public static (int Mems, int Ch) MapInput(int channel)
    {
        var idx = Math.Max(0, channel - 1);
        return (idx / 64 + 1, idx % 64 + 1);
    }

    /// <summary>出光通道 → (mems 基址起, ch 1-64)。</summary>
    public static (int Mems, int Ch) MapOutput(ProductRecipe recipe, int channel)
    {
        var idx = Math.Max(0, channel - 1);
        return (recipe.OutputMemsBase + idx / 64, idx % 64 + 1);
    }

    public static string InputLabel(int channel)
    {
        var (m, c) = MapInput(channel);
        return $"{m}-{c}";
    }
}

internal static class EventRaiseExtensions
{
    public static void Raise<T>(this EventHandler<T>? handler, object sender, T args) =>
        handler?.Invoke(sender, args);
}
