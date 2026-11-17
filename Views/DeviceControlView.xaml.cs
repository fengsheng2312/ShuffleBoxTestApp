using System.Windows.Controls;

namespace ShuffleBoxTestApp.Views;

/// <summary>
/// 设备手动控制页。纯 View：所有交互都走 DeviceControlViewModel 的命令，
/// 这里不做任何业务判断，也不直接访问串口。
/// </summary>
public partial class DeviceControlView : UserControl
{
    public DeviceControlView()
    {
        InitializeComponent();
    }
}
