using System.IO;
using ClosedXML.Excel;
using ShuffleBoxTestApp.Models;

namespace ShuffleBoxTestApp.Services;

/// <summary>
/// 测试报告 xlsx 读写。
/// 默认遍历：新建表，列=通道名称/对应开关/对应输出通道/ADC，重复行标红。
/// 已知遍历：以旧报告为底保留 A-D，新结果写 E(新输出通道)/F(新ADC)，G 记已知通道ADC；
/// 新旧通道不一致整行标红。每次测试生成单独的时间命名文件。
/// </summary>
public interface ITestReportService
{
    Task<IReadOnlyDictionary<int, (int Output, int Adc)>> LoadKnownAsync(string path, CancellationToken ct = default);

    Task<string> SaveAsync(IReadOnlyList<ChannelTestResult> results, string directory,
        TestMode mode, string? knownPath, CancellationToken ct = default);
}

public sealed class TestReportService : ITestReportService
{
    private static readonly string[] DefaultHeaders =
        ["通道名称", "对应开关（1*4开关通道-1*64开关通道）", "对应输出通道", "ADC"];

    public Task<IReadOnlyDictionary<int, (int Output, int Adc)>> LoadKnownAsync(string path, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var map = new Dictionary<int, (int, int)>();
            using var wb = new XLWorkbook(path);
            var ws = wb.Worksheets.First();

            foreach (var row in ws.RowsUsed().Skip(1))
            {
                ct.ThrowIfCancellationRequested();
                var input = ReadInt(row.Cell(1));
                var output = ReadInt(row.Cell(3));
                var adc = ReadInt(row.Cell(4));
                if (input is int inV && inV > 0 && output is int outV && outV > 0)
                {
                    map[inV] = (outV, adc ?? 0);
                }
            }
            return (IReadOnlyDictionary<int, (int Output, int Adc)>)map;
        }, ct);
    }

    public Task<string> SaveAsync(IReadOnlyList<ChannelTestResult> results, string directory,
        TestMode mode, string? knownPath, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"ShuffleTest_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx");

            using var wb = (mode == TestMode.KnownTraversal && knownPath is not null && File.Exists(knownPath))
                ? new XLWorkbook(knownPath)
                : new XLWorkbook();

            var ws = wb.Worksheets.FirstOrDefault() ?? wb.AddWorksheet("Sheet1");

            if (mode == TestMode.KnownTraversal && knownPath is not null && File.Exists(knownPath))
            {
                WriteKnown(ws, results);
            }
            else
            {
                WriteDefault(ws, results);
            }

            ws.Columns().AdjustToContents();
            wb.SaveAs(path);
            return path;
        }, ct);
    }

    private static void WriteDefault(IXLWorksheet ws, IReadOnlyList<ChannelTestResult> results)
    {
        for (var c = 0; c < DefaultHeaders.Length; c++)
        {
            var cell = ws.Cell(1, c + 1);
            cell.Value = DefaultHeaders[c];
            cell.Style.Font.Bold = true;
        }

        var r = 2;
        foreach (var item in results.OrderBy(x => x.InputChannel))
        {
            ws.Cell(r, 1).Value = item.InputChannel;
            ws.Cell(r, 2).Value = item.SwitchLabel;
            ws.Cell(r, 3).Value = item.Found ? item.OutputChannel : "—";
            ws.Cell(r, 4).Value = item.Found ? item.Adc : "—";
            if (item.IsRed) RedRow(ws, r, 4);
            r++;
        }
    }

    private static void WriteKnown(IXLWorksheet ws, IReadOnlyList<ChannelTestResult> results)
    {
        // 保留旧 A-D；E/F 写新结果，G 记已知通道 ADC。
        ws.Cell(1, 5).Value = "新对应输出通道";
        ws.Cell(1, 6).Value = "新ADC";
        ws.Cell(1, 7).Value = "已知通道ADC";
        for (var c = 5; c <= 7; c++) ws.Cell(1, c).Style.Font.Bold = true;

        // 旧表行号映射：进光通道 → 行号。
        var rowByInput = new Dictionary<int, int>();
        foreach (var row in ws.RowsUsed().Skip(1))
        {
            var input = ReadInt(row.Cell(1));
            if (input is int v && v > 0) rowByInput[v] = row.RowNumber();
        }

        var nextRow = ws.LastRowUsed()?.RowNumber() + 1 ?? 2;

        foreach (var item in results.OrderBy(x => x.InputChannel))
        {
            int r;
            if (rowByInput.TryGetValue(item.InputChannel, out var existing))
            {
                r = existing;
            }
            else
            {
                r = nextRow++;
                ws.Cell(r, 1).Value = item.InputChannel;
                ws.Cell(r, 2).Value = item.SwitchLabel;
                ws.Cell(r, 3).Value = item.ExpectedOutput > 0 ? item.ExpectedOutput : "—";
            }

            ws.Cell(r, 5).Value = item.Found ? item.OutputChannel : "—";
            ws.Cell(r, 6).Value = item.Found ? item.Adc : "—";
            ws.Cell(r, 7).Value = item.KnownAdc > 0 ? item.KnownAdc : "—";

            if (item.IsRed) RedRow(ws, r, 7);
        }
    }

    private static void RedRow(IXLWorksheet ws, int row, int lastCol)
    {
        var range = ws.Range(row, 1, row, lastCol);
        range.Style.Fill.BackgroundColor = XLColor.FromHtml("#FFC7CE");
        range.Style.Font.FontColor = XLColor.FromHtml("#9C0006");
    }

    private static int? ReadInt(IXLCell cell)
    {
        if (cell.IsEmpty()) return null;
        if (cell.DataType == XLDataType.Number) return (int)cell.GetDouble();
        var text = cell.GetString().Trim();
        return int.TryParse(text, out var v) ? v : null;
    }
}
