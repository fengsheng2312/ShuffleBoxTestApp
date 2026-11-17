using System.Windows.Controls;

namespace ShuffleBoxTestApp.Views;

/// <summary>
/// 线序测试页。纯 View：布局与绑定都在 LineTestView.xaml，
/// DataContext 由 MainWindow 传入 MainViewModel，这里不含业务逻辑。
/// </summary>
public partial class LineTestView : UserControl
{
    public LineTestView()
    {
        InitializeComponent();
    }
}
