using ShuffleBoxTestApp.Models;

namespace ShuffleBoxTestApp.Services;

/// <summary>
/// 运行日志服务（需求 F-RPT-02 过程日志滚动）。
/// 写入可在任意线程发生，事件由界面层负责转派到 UI 线程并做限流。
/// </summary>
public interface ILogService
{
    /// <summary>新日志产生（可能在后台线程触发）。</summary>
    event EventHandler<LogLine>? LineAdded;

    /// <summary>日志被清空。</summary>
    event EventHandler? Cleared;

    void Info(string text);

    void Warn(string text);

    void Error(string text, Exception? ex = null);

    /// <summary>按指定级别写入，供引擎等只知道级别不知道具体分支的场合使用。</summary>
    void Log(LogLevel level, string text);

    void Clear();
}

/// <summary><see cref="ILogService"/> 的内存实现，后续可在此挂文件落盘与保留天数清理（F-SYS-02）。</summary>
public sealed class LogService : ILogService
{
    public event EventHandler<LogLine>? LineAdded;

    public event EventHandler? Cleared;

    public void Info(string text) => Raise(LogLevel.Info, text);

    public void Warn(string text) => Raise(LogLevel.Warn, text);

    public void Error(string text, Exception? ex = null) =>
        Raise(LogLevel.Error, ex is null ? text : $"{text}：{ex.Message}");

    public void Log(LogLevel level, string text) => Raise(level, text);

    public void Clear() => Cleared?.Invoke(this, EventArgs.Empty);

    private void Raise(LogLevel level, string text) =>
        LineAdded?.Invoke(this, new LogLine(DateTime.Now, level, text));
}
