namespace ShuffleBoxTestApp.Services;

/// <summary>
/// 进光范围表达式解析：支持 "1,3,4"、"1~50"、"1-50" 及混合写法（如 "1,3,5~10"）。
/// 兼容中文逗号/全角波浪号；留空表示全部 1~max。结果去重升序。
/// </summary>
public static class ChannelRangeParser
{
    public static bool TryParse(string? text, int max, out List<int> channels, out string error)
    {
        channels = [];
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(text))
        {
            channels = Enumerable.Range(1, max).ToList();
            return true;
        }

        var normalized = text.Replace('，', ',').Replace('～', '~').Replace('－', '-');
        var set = new HashSet<int>();

        foreach (var part in normalized.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pieces = part.Split(['~', '-'], 2, StringSplitOptions.TrimEntries);

            if (pieces.Length == 1)
            {
                if (!int.TryParse(pieces[0], out var v))
                {
                    error = $"无法识别“{part}”，应为数字或 a~b 范围";
                    return false;
                }

                if (v < 1 || v > max)
                {
                    error = $"通道 {v} 超出 1~{max}";
                    return false;
                }

                set.Add(v);
            }
            else
            {
                if (!int.TryParse(pieces[0], out var a) || !int.TryParse(pieces[1], out var b))
                {
                    error = $"范围“{part}”两端应为整数，如 1~50";
                    return false;
                }

                if (a > b) (a, b) = (b, a);

                if (a < 1 || b > max)
                {
                    error = $"范围 {a}~{b} 超出 1~{max}";
                    return false;
                }

                for (var i = a; i <= b; i++) set.Add(i);
            }
        }

        if (set.Count == 0)
        {
            error = "未解析到任何进光通道";
            return false;
        }

        channels = set.OrderBy(x => x).ToList();
        return true;
    }
}
