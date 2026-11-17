namespace ShuffleBoxTestApp.Models;

/// <summary>日志级别（F-RPT-02 分色显示）。</summary>
public enum LogLevel
{
    Info,
    Warn,
    Error,
}

/// <summary>一条运行日志。</summary>
public sealed record LogLine(DateTime Time, LogLevel Level, string Text)
{
    /// <summary>界面显示的完整文本。</summary>
    public string Line => Time.ToString("[HH:mm:ss.fff] ") + Text;

    /// <summary>级别短标记，便于纯文本导出时辨识。</summary>
    public string LevelTag => Level switch
    {
        LogLevel.Warn => "WARN",
        LogLevel.Error => "ERRO",
        _ => "INFO",
    };
}
