using Avalonia.Controls;
using MFAAvalonia.ViewModels.Pages;

namespace MFAAvalonia.Views.Pages;

public partial class MultiInstanceView : UserControl
{
    public MultiInstanceView()
    {
        // 创建 ViewModel 并设置为 DataContext
        DataContext = new MultiInstanceViewModel();
        InitializeComponent();
    }
}
