using ShuffleBoxTestApp.Models;

namespace ShuffleBoxTestApp.Services;

/// <summary>
/// 主控板应用层协议（需求 F-COM-02 / F-COM-03）。
/// 帧格式：一行一条 ASCII 指令，CRLF 结尾。
///
/// 已确认指令（2026-11-12 硬件侧提供）：
/// - 光开关切换 SMSCH mems_id mems_ch：mems_id 1~16，mems_ch 1~64；
///   成功 OK；命令无识别 Err: Invalid command；参数错误 Err: Invalid para。
/// - PD 读取 GPDADC pd_id：pd_id 1~16；成功如 PD_01 ADC: 0;，ADC 为原始采集值。
///
/// ⚠️ 仍待确认：*IDN? 握手、SYS:STAT? 心跳、MAP:GET? 映射查询沿用示例实现，
/// 拿到正式协议后只需改本类，上层业务与界面不受影响。
/// </summary>
public sealed class MainBoardProtocol
{
    /// <summary>应答成功前缀（SCPI 风格）。</summary>
    private const string OkPrefix = "OK";

    /// <summary>MEMS 开关编号范围（协议确认 1~16）。</summary>
    public const int MemsIdMin = 1;
    public const int MemsIdMax = 16;

    /// <summary>MEMS 通道范围（协议确认 1~64）。</summary>
    public const int MemsChMin = 1;
    public const int MemsChMax = 64;

    /// <summary>PD 编号范围（协议确认 1~16）。</summary>
    public const int PdIdMin = 1;
    public const int PdIdMax = 16;

    /// <summary>应答失败前缀（Err: Invalid command / Err: Invalid para）。</summary>
    private const string ErrorPrefix = "ERR";

    /// <summary>身份查询，返回厂商/型号/序列号/固件版本（F-COM-02 握手）。</summary>
    public string Identify => "*IDN?";

    /// <summary>系统状态查询，返回温度/电压/就绪标志（F-COM-04 心跳）。</summary>
    public string QueryStatus => "SYS:STAT?";

    /// <summary>复位主控板。</summary>
    public string Reset => "*RST";

    /// <summary>
    /// 光开关切换（已确认）：<c>SMSCH mems_id mems_ch</c>。
    /// mems_id 1~16 选择 MEMS 开关，mems_ch 1~64 选择该开关下的通道。
    /// </summary>
    public string SwitchMems(int memsId, int memsCh) => $"SMSCH {memsId} {memsCh}";

    /// <summary>PD 读取（已确认）：<c>GPDADC pd_id</c>，pd_id 1~16。</summary>
    public string ReadPd(int pdId) => $"GPDADC {pdId}";

    /// <summary>
    /// 查询左通道经 Shuffle Box 后实际到达的右通道：<c>MAP:GET? {left}</c>。
    /// ⚠️ 待协议确认：若主控板不直接上报映射，而是需要上位机逐路切换右开关、
    /// 取 PD 最大值作为实际右通道，则改本方法与 <see cref="TryParseRightChannel"/> 的实现，
    /// 上层测试流程无需改动。
    /// </summary>
    public string QueryActualRight(int leftChannel) => $"MAP:GET? {leftChannel}";

    /// <summary>解析实际右通道号。兼容 <c>OK,MAP,137</c>、<c>MAP 137</c>、<c>137</c>。</summary>
    public bool TryParseRightChannel(string response, out int rightChannel)
    {
        rightChannel = 0;

        var body = StripStatus(response);
        if (body is null || !TryFindNumber(body, out var value))
        {
            return false;
        }

        if (double.IsNaN(value) || value < 0)
        {
            return false;
        }

        rightChannel = (int)Math.Round(value);
        return rightChannel > 0;
    }

    /// <summary>
    /// 解析身份应答。兼容 <c>OK,VENDOR,MODEL,SN,FW1.2</c> 与裸 <c>VENDOR,MODEL,SN,FW1.2</c>。
    /// </summary>
    /// <returns>解析成功返回填充好的对象，字段缺失时为 null。</returns>
    public BoardIdentity ParseIdentity(string response)
    {
        var body = StripStatus(response);
        if (body is null)
        {
            return BoardIdentity.Invalid;
        }

        var parts = body.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            return BoardIdentity.Invalid;
        }

        return new BoardIdentity
        {
            Online = true,
            Vendor = NullIfEmpty(parts[0]),
            Model = NullIfEmpty(parts[1]),
            SerialNumber = parts.Length > 2 ? NullIfEmpty(parts[2]) : null,
            Firmware = parts.Length > 3 ? NullIfEmpty(parts[3]) : null,
            RawResponse = response,
        };
    }

    /// <summary>
    /// 解析 PD 原始 ADC 值（已确认格式 <c>PD_01 ADC: 0;</c>）。
    /// 取 "ADC:" 之后、分号之前的整数；兼容无分号结尾。失败帧返回 false。
    /// </summary>
    public bool TryParsePdAdc(string response, out int adc)
    {
        adc = 0;

        var body = StripStatus(response);
        if (body is null)
        {
            return false;
        }

        var idx = body.IndexOf("ADC:", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return false;
        }

        var tail = body[(idx + 4)..].Trim();
        var semi = tail.IndexOf(';');
        if (semi >= 0)
        {
            tail = tail[..semi].Trim();
        }

        return int.TryParse(tail, System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture, out adc);
    }

    /// <summary>
    /// 解析系统状态应答。兼容 <c>OK TEMP=41.2 VOLT=12.05 RDY=1</c>、
    /// <c>41.2,12.05,1</c> 两种写法。
    /// </summary>
    public BoardStatus ParseStatus(string response)
    {
        var body = StripStatus(response);
        if (body is null)
        {
            return BoardStatus.Offline;
        }

        double? temp = null;
        double? volt = null;
        bool? ready = null;

        if (body.Contains('=') || body.Contains(':'))
        {
            // key=value 形式，按分隔符切分后按键名取值。
            var tokens = body.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var token in tokens)
            {
                var kv = token.Split(new[] { '=', ':' }, 2, StringSplitOptions.TrimEntries);
                if (kv.Length != 2)
                {
                    continue;
                }

                if (temp is null && StartsWith(kv[0], "TEMP"))
                {
                    temp = ParseDouble(kv[1]);
                }
                else if (volt is null && (StartsWith(kv[0], "VOLT") || StartsWith(kv[0], "VCC")))
                {
                    volt = ParseDouble(kv[1]);
                }
                else if (ready is null && (StartsWith(kv[0], "RDY") || StartsWith(kv[0], "READY")
                                           || StartsWith(kv[0], "STS") || StartsWith(kv[0], "STA")))
                {
                    ready = ParseBool(kv[1]);
                }
            }
        }
        else
        {
            // 位置形式：温度, 电压, 就绪。
            var parts = body.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0)
            {
                temp = ParseDouble(parts[0]);
            }

            if (parts.Length > 1)
            {
                volt = ParseDouble(parts[1]);
            }

            if (parts.Length > 2)
            {
                ready = ParseBool(parts[2]);
            }
        }

        // 只要拿到任一项就认为板卡在线（应答本身就是在线证据）。
        return new BoardStatus
        {
            Online = true,
            Temperature = temp,
            Voltage = volt,
            Ready = ready ?? true,
            RawResponse = response,
        };
    }

    /// <summary>判断应答是否为失败帧。</summary>
    public bool IsError(string response) =>
        response.StartsWith(ErrorPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>剥掉 OK/ERROR 状态字，返回数据段；失败帧返回 null。</summary>
    private static string? StripStatus(string response)
    {
        var text = response.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        if (text.StartsWith(ErrorPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (text.StartsWith(OkPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var rest = text[OkPrefix.Length..].Trim();

            // "OK:" / "OK," 这类纯状态应答，数据段为空。
            if (rest.Length == 0)
            {
                return string.Empty;
            }

            if (rest[0] is ':' or ',')
            {
                rest = rest[1..].Trim();
            }

            return rest;
        }

        return text;
    }

    /// <summary>
    /// 在文本中找第一个可解析的浮点数。
    /// 用 SubString 逐段试探而不是正则，避免协议数值带单位后缀时整体解析失败。
    /// </summary>
    private static bool TryFindNumber(string text, out double value)
    {
        value = double.NaN;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var start = -1;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var isNumberChar = char.IsDigit(c) || c is '.' or '-' or '+';

            if (isNumberChar && start < 0)
            {
                start = i;
            }
            else if (!isNumberChar && start >= 0)
            {
                if (TryParseDouble(text[start..i], out value))
                {
                    return true;
                }

                start = -1;
            }
        }

        if (start >= 0 && TryParseDouble(text[start..], out value))
        {
            return true;
        }

        value = double.NaN;
        return false;
    }

    private static bool StartsWith(string input, string prefix) =>
        input.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    private static bool TryParseDouble(string text, out double value) =>
        double.TryParse(text, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out value);

    private static double? ParseDouble(string text) =>
        TryParseDouble(text, out var v) ? v : null;

    private static bool? ParseBool(string text)
    {
        if (bool.TryParse(text, out var b))
        {
            return b;
        }

        if (TryParseDouble(text, out var n))
        {
            return n > 0.5;
        }

        return null;
    }

    private static string? NullIfEmpty(string text) => string.IsNullOrWhiteSpace(text) ? null : text;
}
