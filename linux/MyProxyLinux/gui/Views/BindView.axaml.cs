using Avalonia.Controls;

namespace MyProxy.Gui.Views;

/// <summary>
/// 绑定页的代码后置。除 <c>InitializeComponent</c> 之外只有一件事：把配对码输入行为挂到
/// XAML 里那个裸 <see cref="TextBox"/> 上——与 Windows 端 <c>Views/BindView.xaml.cs</c> 的
/// <c>_ = new PairingCodeInput(PairingCodeBox);</c> 一一对应。
///
/// <para>
/// Windows 端的配对码输入是「TextBox + 挂载式行为类（<c>Views/PairingCodeInput.cs</c>）」；
/// Avalonia 端的同一套行为已经写在 <see cref="PairingCodeInput"/> 里（同一个类名，
/// 同时提供「派生控件」与「<see cref="PairingCodeInput.Attach"/> 挂载」两种用法）。
/// 这里选挂载：XAML 保持 Windows 的样子（裸 TextBox + 同一个 <c>x:Name</c>），
/// 行为在 code-behind 挂上去，将来要换回派生控件只需改这两处。
/// </para>
///
/// <para>
/// 两条与 Windows 端不同的地方，都在 <see cref="PairingCodeInput"/> 里说明了，
/// 这里留个索引免得看代码时找不到：
/// <list type="bullet">
/// <item><c>CharacterCasing="Upper"</c> 在 Avalonia 没有同名属性，自动大写由共享的
/// <c>PairingCodeFormatter</c> 保证（结果一致，机制不同）。</item>
/// <item>错误态不是 <c>Style.Triggers</c> 读 <c>Tag</c>，而是样式里的属性选择器
/// <c>[Tag=True]</c>（见 Themes/InputAndButtonStyles.axaml）；视图侧照旧只写
/// <c>Tag="{Binding HasCodeError}"</c> 这一条。</item>
/// </list>
/// </para>
/// </summary>
public partial class BindView : UserControl
{
    public BindView()
    {
        InitializeComponent();

        // 单向挂载：订阅随 TextBox 活到进程结束，与 Windows 端一样没有卸载入口。
        // 因此这里只调用一次，且不要对 PairingCodeInput 自身再调用（见其类注释）。
        PairingCodeInput.Attach(PairingCodeBox);
    }
}
