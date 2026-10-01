using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace MyProxy.Gui.Views;

/// <summary>
/// 设置页。与 Windows 端的 <c>SettingsView.xaml.cs</c> 一样，这里**没有业务逻辑**：
/// 开关、主题选择、版本与错误文案全是绑定，Escape 关页走 XAML 的 KeyBindings + CloseCommand。
///
/// <para>
/// 唯一多出来的一件事是「重新绑定」前的那句确认。Windows 端这一步在
/// <c>SettingsViewModel.RebindAsync</c> 里用 <c>MessageBox.Show(..., OKCancel, Question)</c> 完成；
/// Linux 的 ViewModel 不弹窗（它不认识窗口），所以确认落在视图侧——见
/// <see cref="OnRebindClicked"/>。
/// </para>
/// </summary>
public sealed partial class SettingsView : UserControl
{
    /// <summary>确认框里那段正文的 XAML 键（内容在 <c>SettingsView.axaml</c> 的 Resources 里）。</summary>
    private const string RebindConfirmContentKey = "RebindConfirmContent";

    public SettingsView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// 「重新绑定」的点击处理：先确认，确认通过后才执行绑定在按钮上的 <c>RebindCommand</c>。
    ///
    /// <para>
    /// 为什么要把命令的执行权拿过来：确认是异步的，而 Avalonia 11 的
    /// <c>Button.OnClick</c> 会在 Click 处理器返回之后立刻执行绑定命令
    /// （判据是 <c>!e.Handled &amp;&amp; command is not null &amp;&amp; command.CanExecute(...)</c>）。
    /// 所以这里先把这次点击标成已处理，Button 就不会自动执行命令；用户点了「确定」之后
    /// 由本方法亲自执行那一条命令。于是「没确认就解绑」和「确认后执行两次」都不可能发生。
    /// </para>
    ///
    /// <para>
    /// 命令本身仍然来自 XAML 的 <c>Command="{Binding RebindCommand}"</c>（绑定名与 Windows 端一致），
    /// 这里只是从按钮上把它读回来——视图不需要认识 ViewModel 的类型。
    /// </para>
    /// </summary>
    private async void OnRebindClicked(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;

        if (sender is not Button button)
        {
            return;
        }

        // 没有宿主窗口就没有可以模态依附的父窗。宁可不做，也不要在用户看不见确认的情况下解绑设备。
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        if (!await ConfirmRebindAsync(owner))
        {
            return;
        }

        ICommand? command = button.Command;
        if (command is not null && command.CanExecute(button.CommandParameter))
        {
            command.Execute(button.CommandParameter);
        }
    }

    /// <summary>
    /// 弹一个模态确认框，返回用户是否确认。窗口的关闭按钮等于「取消」（<c>ShowDialog&lt;bool&gt;</c>
    /// 在窗口没给结果时返回 <c>false</c>），所以误关不会导致解绑。
    /// </summary>
    private async Task<bool> ConfirmRebindAsync(Window owner)
    {
        if (!this.TryFindResource(RebindConfirmContentKey, out object? content) ||
            content is not IDataTemplate template ||
            template.Build(null) is not Control dialogContent)
        {
            // 内容缺失是主题/资源装配错误：这里只能拒绝，不能当成「用户同意」。
            return false;
        }

        // 两个按钮靠 IsDefault / IsCancel 自我标识，Click 冒泡到内容根上收口，
        // 于是模板里不需要名字、也不需要挂事件处理器。
        dialogContent.AddHandler(Button.ClickEvent, OnConfirmDialogButtonClicked);

        var dialog = new Window
        {
            Title = AppInfo.ProductName,
            Content = dialogContent,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        return await dialog.ShowDialog<bool>(owner);
    }

    private static void OnConfirmDialogButtonClicked(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not Button button)
        {
            return;
        }

        // 「确定」是 IsDefault 那个，「取消」是 IsCancel 那个（见 SettingsView.axaml）。
        if (TopLevel.GetTopLevel(button) is Window dialog)
        {
            dialog.Close(button.IsDefault);
        }
    }
}
