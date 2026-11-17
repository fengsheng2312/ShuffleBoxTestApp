namespace ShuffleBoxTestApp.Models;

/// <summary>
/// 单通道测试结果行。列对齐模板：通道名称/对应开关/对应输出通道/ADC。
/// 已知遍历额外携带：预期(旧)输出、已知通道 ADC、是否直接命中。
/// IsRed 统一表示异常标红：默认遍历=出光重复；已知遍历=新旧通道不一致。
/// </summary>
public sealed class ChannelTestResult : System.ComponentModel.INotifyPropertyChanged
{
    private bool _isRed;
    private string _remark = string.Empty;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    /// <summary>进光通道号（1~256），模板"通道名称"。</summary>
    public int InputChannel { get; init; }

    /// <summary>进光开关 "mems-ch"（1-1 ~ 4-64），模板"对应开关"。</summary>
    public string SwitchLabel { get; init; } = string.Empty;

    /// <summary>最终记录的输出通道（实际找到；未找到为 0）。</summary>
    public int OutputChannel { get; init; }

    /// <summary>最终记录输出通道时的 ADC 回读值。</summary>
    public int Adc { get; init; }

    /// <summary>已知遍历的预期(旧报告)输出通道；默认遍历为 0。</summary>
    public int ExpectedOutput { get; init; }

    /// <summary>已知遍历切到预期出光时的 ADC 回读值（用于判断是否达标）。</summary>
    public int KnownAdc { get; init; }

    /// <summary>已知遍历是否直接命中预期（未走全遍历）。</summary>
    public bool MetDirect { get; init; }

    public bool Found => OutputChannel > 0;

    /// <summary>异常标红：默认=出光重复；已知=新旧通道不一致。</summary>
    public bool IsRed
    {
        get => _isRed;
        set
        {
            if (_isRed == value) return;
            _isRed = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsRed)));
        }
    }

    public string Remark
    {
        get => _remark;
        set
        {
            if (_remark == value) return;
            _remark = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Remark)));
        }
    }

    public string OutputText => Found ? OutputChannel.ToString() : "—";
    public string AdcText => Found ? Adc.ToString() : "—";
    public string ExpectedText => ExpectedOutput > 0 ? ExpectedOutput.ToString() : "—";
    public string KnownAdcText => KnownAdc > 0 ? KnownAdc.ToString() : "—";
}
