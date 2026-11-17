using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using ShuffleBoxTestApp.Services;
using ShuffleBoxTestApp.ViewModels;

namespace ShuffleBoxTestApp;

/// <summary>
/// 应用入口与依赖注入组合根。
/// 分层顺序：View（XAML）→ ViewModel → 业务服务（主控板/测试引擎/线序表）→ 传输层（串口）。
/// 上层只依赖下层的接口，具体实现在此处集中登记，替换实现不影响调用方。
/// </summary>
public partial class App : Application
{
    private ServiceProvider? _provider;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection();

        // 界面线程调度器：必须用当前 UI 线程的 Dispatcher 构造，故在此登记。
        services.AddSingleton<IUiDispatcher>(new UiDispatcher(Dispatcher));
        services.AddSingleton<ILogService, LogService>();

        // 传输层
        services.AddSingleton<ISerialPortService, SerialPortService>();
        services.AddSingleton<IPortInfoProvider, WmiPortInfoProvider>();

        // 协议层（无状态，可共享）
        services.AddSingleton<MainBoardProtocol>();

        // 业务层
        services.AddSingleton<IGoldenListService, GoldenListService>();
        services.AddSingleton<IMainBoardService, MainBoardService>();
        services.AddSingleton<ITestReportService, TestReportService>();

        // 配方单例：页面编辑阈值/模式/报告目录，引擎读取同一实例。
        services.AddSingleton<Models.ProductRecipe>();
        services.AddSingleton<ITestEngine>(sp => new TestEngine(
            sp.GetRequiredService<IMainBoardService>(),
            sp.GetRequiredService<ILogService>(),
            () => sp.GetRequiredService<Models.ProductRecipe>()));

        // 视图与视图模型（一页一 VM）
        services.AddSingleton<DeviceControlViewModel>();
        services.AddSingleton<LineTestViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();

        _provider = services.BuildServiceProvider();

        var window = _provider.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Closed += (_, _) => Shutdown();
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 需求 F-ERR-04 优雅退出：先取消进行中的操作，再让容器逐个 Dispose 释放串口句柄。
        try
        {
            _provider?.GetService<MainViewModel>()?.Dispose();
            _provider?.GetService<DeviceControlViewModel>()?.Dispose();
            _provider?.GetService<IMainBoardService>()?.Dispose();
        }
        catch
        {
            // 退出阶段的释放异常不阻塞进程结束。
        }

        _provider?.Dispose();
        base.OnExit(e);
    }
}