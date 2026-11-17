namespace ShuffleBoxTestApp.Services;

/// <summary>
/// 界面线程调度器。串口层的事件都在后台线程触发，ViewModel 借此把
/// 集合与属性更新安全地投递回界面线程（需求 F-UI-03 异步无阻塞交互）。
/// </summary>
public interface IUiDispatcher
{
    /// <summary>把动作排入界面线程队列，不等待执行完成。用于高频日志/进度。</summary>
    void Post(Action action);

    /// <summary>在界面线程同步执行并等待完成。用于必须立即生效的状态切换。</summary>
    Task InvokeAsync(Action action);
}

/// <summary>基于 <see cref="System.Windows.Threading.Dispatcher"/> 的实现。</summary>
public sealed class UiDispatcher : IUiDispatcher
{
    private readonly System.Windows.Threading.Dispatcher _dispatcher;

    public UiDispatcher(System.Windows.Threading.Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public void Post(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            TryRun(action);
            return;
        }

        // Background 优先级：保证日志与进度不会饿死按钮点击等输入。
        _ = _dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, action);
    }

    public Task InvokeAsync(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            TryRun(action);
            return Task.CompletedTask;
        }

        return _dispatcher.InvokeAsync(action).Task;
    }

    private static void TryRun(Action action)
    {
        try
        {
            action();
        }
        catch
        {
            // 界面刷新失败不能反向影响串口与测试流程。
        }
    }
}
