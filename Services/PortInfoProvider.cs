using System.Management;
using System.Text.RegularExpressions;
using ShuffleBoxTestApp.Models;

namespace ShuffleBoxTestApp.Services;

/// <summary>
/// 串口枚举与驱动信息查询（界面串口下拉框数据源）。
/// 与传输层分离：传输层只需 <see cref="ISerialPortService.GetAvailablePorts"/> 拿裸口号，
/// 界面需要驱动信息时走本服务，两者互不依赖。
/// </summary>
public interface IPortInfoProvider
{
    /// <summary>
    /// 获取全部串口及其驱动信息。WMI 查询较慢（首次可达数百毫秒），
    /// 实现内部在后台线程执行并带短期缓存，调用方可直接在界面线程 await。
    /// </summary>
    Task<IReadOnlyList<SerialPortInfo>> GetPortsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 基于 WMI（Win32_PnPEntity）的实现。
///
/// 为什么不用注册表直读：实测 HKLM\SYSTEM\CurrentControlSet\Enum 下的 Driver 子键
/// 在本机受访问限制读不到 DriverDesc/InfPath，而 WMI 能稳定拿到设备描述与驱动服务名。
/// </summary>
public sealed partial class WmiPortInfoProvider : IPortInfoProvider
{
    /// <summary>缓存有效期。下拉框每次展开都会请求，避免频繁全量 WMI 查询拖慢界面。</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(3);

    private static readonly object QueryLock = new();

    private static IReadOnlyList<SerialPortInfo>? _cache;
    private static DateTime _cacheAt = DateTime.MinValue;

    public async Task<IReadOnlyList<SerialPortInfo>> GetPortsAsync(CancellationToken cancellationToken = default)
    {
        // 缓存命中直接返回，不占用查询锁。
        if (_cache is { Count: > 0 } cached && DateTime.UtcNow - _cacheAt < CacheTtl)
        {
            return cached;
        }

        return await Task.Run(() => Query(cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>强制丢弃缓存，用于“刷新”按钮或插拔设备后立即重新枚举。</summary>
    public static void InvalidateCache()
    {
        lock (QueryLock)
        {
            _cache = null;
            _cacheAt = DateTime.MinValue;
        }
    }

    private static IReadOnlyList<SerialPortInfo> Query(CancellationToken cancellationToken)
    {
        lock (QueryLock)
        {
            if (_cache is { Count: > 0 } cached && DateTime.UtcNow - _cacheAt < CacheTtl)
            {
                return cached;
            }

            var live = ReadLivePorts();
            var byPort = MapByPortName(cancellationToken);

            var list = new List<SerialPortInfo>(live.Count + byPort.Count);

            foreach (var port in live)
            {
                // 驱动信息查不到时（虚拟口、非标准枚举），降级为只有口号的条目，保证可用性。
                list.Add(byPort.TryGetValue(port, out var info)
                    ? info with { PortName = port, HasDriverInfo = true }
                    : new SerialPortInfo { PortName = port, Description = "系统未报告设备描述", Status = "Unknown" });
            }

            // 设备已装驱动但当前未分配 COM 号的（例如被占用、端口号被改）不显示，
            // 否则下拉框会出现打不开的空口，反而干扰操作员。
            list.Sort(static (a, b) =>
            {
                var byNumber = PortNumber(a.PortName).CompareTo(PortNumber(b.PortName));
                return byNumber != 0 ? byNumber : string.CompareOrdinal(a.PortName, b.PortName);
            });

            _cache = list;
            _cacheAt = DateTime.UtcNow;
            return list;
        }
    }

    private static IReadOnlyList<string> ReadLivePorts()
    {
        try
        {
            return System.IO.Ports.SerialPort.GetPortNames()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>查询系统串口设备，按 COM 号建索引。</summary>
    private static Dictionary<string, SerialPortInfo> MapByPortName(CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, SerialPortInfo>(StringComparer.OrdinalIgnoreCase);

        try
        {
            // 只查 Ports 类设备，字段限定，避免全表枚举拖慢查询。
            const string wql =
                "SELECT Name, Description, Manufacturer, DeviceID, Service, Status, ConfigManagerErrorCode " +
                "FROM Win32_PnPEntity WHERE PNPClass='Ports'";

            using var searcher = new ManagementObjectSearcher(wql);
            using var results = searcher.Get();

            foreach (ManagementObject mo in results)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var name = GetString(mo, "Name");
                var port = MatchPort(name) ?? MatchPort(GetString(mo, "Description"));
                if (port is null)
                {
                    // 主板自带但未启用的并口设备等没有 COM 号，跳过。
                    continue;
                }

                var description = FirstNonEmpty(GetString(mo, "Description"), name);

                map[port] = new SerialPortInfo
                {
                    PortName = port,
                    Description = string.IsNullOrEmpty(description) ? name : StripPortSuffix(description, port),
                    DriverService = GetString(mo, "Service"),
                    Manufacturer = GetString(mo, "Manufacturer"),
                    DeviceId = GetString(mo, "DeviceID"),
                    Status = FirstNonEmpty(GetString(mo, "Status"), "Unknown"),
                    ConfigErrorCode = GetUInt32(mo, "ConfigManagerErrorCode"),
                };
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // WMI 服务异常或被策略禁用时返回空表，上层降级为只显示 COM 号。
        }

        return map;
    }

    /// <summary>从 “xxx (COM7)” 里取出 COM7。</summary>
    private static string? MatchPort(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var match = PortPattern().Match(text);
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
    }

    /// <summary>去掉描述尾部重复的 “(COM7)”。</summary>
    private static string StripPortSuffix(string description, string port)
    {
        var open = description.LastIndexOf('(');
        if (open <= 0 || !description.EndsWith(")", StringComparison.Ordinal))
        {
            return description.Trim();
        }

        var inner = description[(open + 1)..^1].Trim();
        return inner.Equals(port, StringComparison.OrdinalIgnoreCase)
            ? description[..open].TrimEnd()
            : description.Trim();
    }

    private static string GetString(ManagementObject mo, string property)
    {
        try
        {
            return mo[property]?.ToString()?.Trim() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static uint GetUInt32(ManagementObject mo, string property)
    {
        try
        {
            var raw = mo[property];
            return raw is null ? 0u : Convert.ToUInt32(raw);
        }
        catch
        {
            return 0u;
        }
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;

    private static int PortNumber(string portName)
    {
        var digits = new string(portName.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) ? n : int.MaxValue;
    }

    [GeneratedRegex(@"\b(COM\d+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PortPattern();
}
