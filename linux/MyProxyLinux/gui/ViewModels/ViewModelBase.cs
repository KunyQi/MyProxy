using System.ComponentModel;
using System.Runtime.CompilerServices;

// 命名空间与 gui/MainWindow.axaml.cs、App.axaml 的 vm: 前缀一致（MyProxy.Gui.ViewModels），
// 而不是 Windows 端的 MyProxy.ViewModels：这个工程的 RootNamespace 是 MyProxy.Gui。
namespace MyProxy.Gui.ViewModels;

/// <summary>
/// 视图模型的基类：逐字搬自 <c>windows/MyProxy/ViewModels/ViewModelBase.cs</c>，
/// 只去掉 WPF 的依赖。
///
/// <para>
/// Avalonia 的绑定引擎要的只有 <see cref="INotifyPropertyChanged"/>——它不认识 WPF 的
/// <c>DependencyObject</c>，也不需要视图模型是它。所以这份基类与 Windows 端**同形**：
/// 同样的两个受保护成员（<see cref="OnPropertyChanged"/> / <see cref="SetProperty"/>）、
/// 同样的 <c>[CallerMemberName]</c> 约定、同样的「相等就不发通知」语义。
/// 三个页面 ViewModel（主页 / 绑定 / 设置）都从这里派生，于是「先比较再通知」这条
/// 规矩不必在每处重写一遍。
/// </para>
///
/// <para>
/// <b>Windows 端的一处差异必须记住：</b>那边的可见性属性返回 <c>System.Windows.Visibility</c>
/// （三态：<c>Visible</c> / <c>Hidden</c> / <c>Collapsed</c>），Avalonia 只有布尔的
/// <c>IsVisible</c>。移植时保留属性名、把类型换成 <c>bool</c>，但 WPF 的 <c>Hidden</c>
/// 语义（不画但**占位**）在 Avalonia 里没有对应物——凡是从 <c>Hidden</c> 映射过来的
/// 布尔属性，视图侧必须自己把那一格保住（见 <c>MainViewModel.CheckButtonVisibility</c>
/// 与 <c>TrafficVisibility</c> 的注释）。
/// </para>
/// </summary>
public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
