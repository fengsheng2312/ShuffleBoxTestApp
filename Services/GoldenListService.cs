using System.Globalization;
using System.IO;
using System.Text;

namespace ShuffleBoxTestApp.Services;

/// <summary>线序比对结果（需求模块③ 线序校验）。</summary>
public enum ExpectedResult
{
    /// <summary>线序表中没有该通道的记录。</summary>
    Unknown,

    /// <summary>该通道被线序表显式标记为不导通（预期断路）。</summary>
    ShouldBeOpen,

    /// <summary>预期导通到某个右通道。</summary>
    Connected,
}

/// <summary>
/// 标准线序表（Golden List，需求 F-CHK-01）。
/// 数据服务，只回答“预期是什么”，不参与判定与界面。
/// </summary>
public interface IGoldenListService
{
    /// <summary>当前数据源名称，用于日志与报告标注比对基准。</summary>
    string SourceName { get; }

    /// <summary>线序表覆盖的通道数。</summary>
    int ChannelCount { get; }

    /// <summary>线序表条目总数（含标记为断路的项）。</summary>
    int EntryCount { get; }

    /// <summary>加载 CSV 线序表并校验格式，校验失败抛 <see cref="InvalidDataException"/>。</summary>
    Task LoadFromCsvAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 使用恒等映射（左 N → 右 N）作为基准。
    /// 仅用于线序表尚未导入时的联调，正式生产必须导入 Golden List。
    /// </summary>
    void UseIdentity(int channelCount);

    /// <summary>清空为“无线序表”状态。</summary>
    void Clear();

    /// <summary>查询左通道的预期结果。</summary>
    ExpectedResult GetExpected(int leftChannel, out int expectedRightChannel);
}

/// <summary>
/// CSV 线序表实现。
///
/// 文件格式：UTF-8/GBK 自动识别，允许 BOM、# 或 // 注释行、空行；
/// 每行至少两列整数（左通道, 预期右通道）。右通道留空或填 0 表示该路预期断路。
/// </summary>
public sealed class GoldenListService : IGoldenListService
{
    /// <summary>单文件行数上限，防止误传超大文件把内存吃满。</summary>
    private const int MaxLines = 200_000;

    /// <summary>通道号上限，与产品最大规格对齐。</summary>
    private const int MaxChannel = 4096;

    private static readonly string[] Separators = [",", ";", "\t", "|"];

    private Dictionary<int, int> _map = new();
    private volatile int _identityCount;

    public string SourceName { get; private set; } = "未加载";

    public int ChannelCount => _map.Count > 0 ? _map.Keys.Max() : _identityCount;

    public int EntryCount => _map.Count > 0 ? _map.Count : _identityCount;

    public async Task LoadFromCsvAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (File.Exists(filePath))
        {
            var text = await Task.Run(() => ReadAllTextSmart(filePath), cancellationToken).ConfigureAwait(false);
            var map = Parse(text, filePath);
            Interlocked.Exchange(ref _identityCount, 0);
            _map = map;
            SourceName = Path.GetFileName(filePath);
            return;
        }

        // 配方里可以只写内联数据（如 "1=1,2=3,3=2"），便于把小配方直接嵌进配置。
        var inline = await Task.Run(() => ReadInlineFallback(filePath), cancellationToken).ConfigureAwait(false);
        if (inline is not null)
        {
            var map = Parse(inline, "配方内联数据");
            Interlocked.Exchange(ref _identityCount, 0);
            _map = map;
            SourceName = "配方内联数据";
            return;
        }

        throw new FileNotFoundException($"找不到线序表文件：{filePath}", filePath);
    }

    public void UseIdentity(int channelCount)
    {
        _map = new Dictionary<int, int>();
        Interlocked.Exchange(ref _identityCount, Math.Max(0, channelCount));
        SourceName = "内置恒等映射（未导入 Golden List）";
    }

    public void Clear()
    {
        _map = new Dictionary<int, int>();
        Interlocked.Exchange(ref _identityCount, 0);
        SourceName = "未加载";
    }

    public ExpectedResult GetExpected(int leftChannel, out int expectedRightChannel)
    {
        if (_map.Count > 0)
        {
            if (!_map.TryGetValue(leftChannel, out var right))
            {
                expectedRightChannel = 0;
                return ExpectedResult.Unknown;
            }

            expectedRightChannel = right;
            return right > 0 ? ExpectedResult.Connected : ExpectedResult.ShouldBeOpen;
        }

        if (_identityCount > 0)
        {
            if (leftChannel > _identityCount)
            {
                expectedRightChannel = 0;
                return ExpectedResult.Unknown;
            }

            expectedRightChannel = leftChannel;
            return ExpectedResult.Connected;
        }

        expectedRightChannel = 0;
        return ExpectedResult.Unknown;
    }

    private static Dictionary<int, int> Parse(string text, string source)
    {
        var map = new Dictionary<int, int>();
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var headerSkipped = false;

        for (var i = 0; i < lines.Length; i++)
        {
            if (i >= MaxLines)
            {
                throw new InvalidDataException($"{source} 超过 {MaxLines} 行上限，请确认文件是否为线序表");
            }

            var line = lines[i].Trim().TrimStart('\uFEFF');

            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            // 第一行若含非数字（表头），跳过后不再尝试。
            if (!headerSkipped)
            {
                headerSkipped = true;
                if (line.Any(char.IsLetter))
                {
                    continue;
                }
            }

            var columns = line.Split(Separators, StringSplitOptions.TrimEntries);
            if (columns.Length < 2)
            {
                throw new InvalidDataException($"{source} 第 {i + 1} 行只有 {columns.Length} 列，至少需要“左通道, 预期右通道”两列：{line}");
            }

            if (!int.TryParse(columns[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var left))
            {
                throw new InvalidDataException($"{source} 第 {i + 1} 行左通道号不是整数：{columns[0]}");
            }

            if (left <= 0 || left > MaxChannel)
            {
                throw new InvalidDataException($"{source} 第 {i + 1} 行左通道号 {left} 超出 1~{MaxChannel} 范围");
            }

            var rightText = columns[1];
            var right = 0;
            if (!string.IsNullOrEmpty(rightText) && !IsBlankMarker(rightText))
            {
                if (!int.TryParse(rightText, NumberStyles.Integer, CultureInfo.InvariantCulture, out right))
                {
                    throw new InvalidDataException($"{source} 第 {i + 1} 行预期右通道不是整数：{rightText}");
                }

                if (right < 0 || right > MaxChannel)
                {
                    throw new InvalidDataException($"{source} 第 {i + 1} 行预期右通道 {right} 超出 0~{MaxChannel} 范围");
                }
            }

            if (!map.TryAdd(left, right))
            {
                throw new InvalidDataException($"{source} 第 {i + 1} 行左通道 {left} 重复定义，请检查文件");
            }
        }

        if (map.Count == 0)
        {
            throw new InvalidDataException($"{source} 没有解析到任何有效线序数据行");
        }

        return map;
    }

    private static bool IsBlankMarker(string text) =>
        text is "-" or "NC" or "nc" or "N/C" or "N/A" or "NA" or "/";

    /// <summary>
    /// 优先按 UTF-8 严格解码，失败退回 GBK。
    /// 国产仪器与工厂 Excel 导出的表常见 GBK，直接按 UTF-8 读会出现乱码列名导致解析失败。
    /// </summary>
    private static string ReadAllTextSmart(string filePath)
    {
        try
        {
            return File.ReadAllText(filePath, Encoding.UTF8);
        }
        catch (DecoderFallbackException)
        {
            try
            {
                return File.ReadAllText(filePath, Encoding.GetEncoding("GB2312"));
            }
            catch
            {
                return File.ReadAllText(filePath);
            }
        }
    }

    private static string? ReadInlineFallback(string spec)
    {
        if (string.IsNullOrWhiteSpace(spec) || !spec.Contains('='))
        {
            return null;
        }

        var builder = new StringBuilder();
        foreach (var pair in spec.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2, StringSplitOptions.TrimEntries);
            if (kv.Length != 2)
            {
                return null;
            }

            builder.Append(kv[0]).Append(',').Append(kv[1]).Append('\n');
        }

        return builder.ToString();
    }
}
