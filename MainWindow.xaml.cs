using System.Collections.Specialized;
using System.Windows.Controls;
using System.Windows.Input;
using ShuffleBoxTestApp.ViewModels;

namespace ShuffleBoxTestApp;

/// <summary>
/// 主窗口。View 层不含业务逻辑，仅：
/// 1. 由组合根注入 DataContext；
/// 2. 处理两件纯视图行为——日志自动滚动、扫码枪回车提交。
/// </summary>
public partial class MainWindow : HandyControl.Controls.Window
{
    public MainWindow(MainViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();

        // 日志有新行时滚到最新（F-RPT-02 自动滚动到最新行）。
        if (LogList.Items is INotifyCollectionChanged collector)
        {
            collector.CollectionChanged += (_, _) =>
            {
                if (LogList.Items.Count > 0)
                {
                    LogList.ScrollIntoView(LogList.Items[^1]);
                }
            };
        }
    }

    /// <summary>SN 输入框回车提交（扫码枪默认以回车结尾，故扫码即自动绑定 SN）。</summary>
    private void SnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box || DataContext is not MainViewModel vm)
        {
            return;
        }

        vm.SnInput = box.Text;
        vm.CommitSerialNumberCommand.Execute(null);
        box.Clear();
    }
}