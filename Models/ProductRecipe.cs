namespace ShuffleBoxTestApp.Models;

/// <summary>遍历测试模式。</summary>
public enum TestMode
{
    /// <summary>默认遍历：适配未知 Shuffle Box，进光×出光全遍历。</summary>
    DefaultTraversal,

    /// <summary>已知遍历：读旧报告，先试预期出光，不达标再全遍历。</summary>
    KnownTraversal,
}

/// <summary>
/// 产品测试配方。进光=MEMS 1-4（1-1~4-64）；出光=另一组 MEMS（默认基址 5，即 5-1~8-64）。
/// 读数为固定 PD（默认 1）。阈值/基址/PD 均可在页面设置。
/// </summary>
public sealed class ProductRecipe
{
    public string Name { get; set; } = "SB-256-RevA";

    /// <summary>进光通道总数（1~256）。</summary>
    public int ChannelCount { get; set; } = 256;

    /// <summary>出光通道总数（1~256）。</summary>
    public int OutputCount { get; set; } = 256;

    /// <summary>ADC 判定阈值，读数大于该值认为找到通道（页面可设置）。</summary>
    public int PdThresholdAdc { get; set; } = 1000;

    /// <summary>出光 MEMS 基址：出光通道 n → mems = 基址 + (n-1)/64。默认 5。</summary>
    public int OutputMemsBase { get; set; } = 5;

    /// <summary>切换后读取的 PD 编号（1~16）。</summary>
    public int PdId { get; set; } = 1;

    /// <summary>光开关切换后的等待稳定时间（毫秒）。</summary>
    public int SwitchSettleMs { get; set; } = 15;

    public TestMode Mode { get; set; } = TestMode.DefaultTraversal;

    /// <summary>测试报告输出目录。</summary>
    public string ReportDirectory { get; set; } = @"D:\ShuffleTest";
}
