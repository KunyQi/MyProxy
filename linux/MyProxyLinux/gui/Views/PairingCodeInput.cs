using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using MyProxy.Core;

namespace MyProxy.Gui.Views;

/// <summary>
/// 配对码输入框。**它不是 8 个独立格子**：Windows 端就是一个 <see cref="TextBox"/> 加
/// <c>Views/PairingCodeInput</c> 这个挂载式行为类，8 位码始终是**一串带分隔符的文本**
/// （<c>A7K9-M2QF</c>，最长 9 字符）。逐格填入在这里不存在，也不该被发明出来——
/// 那会让两端的行为、<c>MaxLength</c> 语义与绑定名全部对不上。
///
/// <para>
/// 移植方式：Windows 端「TextBox + 行为类」在 Avalonia 里合成**一个派生自
/// <see cref="TextBox"/> 的控件**（<see cref="TextBox"/> 本身就是 TemplatedControl）。
/// 这样做的唯一理由是把 Windows 端的对外表面整块保住：属性名逐字相同
/// （<c>Text</c> / <c>MaxLength</c> / <c>CaretIndex</c> / <c>SelectionStart</c> /
/// <c>SelectionEnd</c> / <c>TextAlignment</c> / <c>IsEnabled</c> / <c>Tag</c> /
/// <c>Classes</c>），而且按 TextBox 写好的 ControlTheme（<c>TextInputStyle</c>）、
/// 以及绑在 <c>Tag</c> 上的错误态，都不需要任何转接就能继续用。
/// 若改成一个「包着 TextBox 的 UserControl」，这些名字全都要手工再暴露一遍，
/// 而 <c>CharacterCasing</c> 在 Avalonia 里根本没有同名物（见下），包一层只会多出一层对不上的表面。
/// </para>
///
/// <para>
/// <b>行为对照（Windows → 本控件，逐条）</b>
/// </para>
/// <list type="number">
///   <item><description>
///     <b>整串粘贴</b>：Windows 用 <c>DataObject.AddPastingHandler</c> 把待粘贴的数据对象
///     换成清洗过的串，再让 TextBox 原生插入。Avalonia 没有 DataObject 粘贴管线，替代物是
///     <see cref="TextBox.PastingFromClipboard"/>（<c>Handled = true</c> 取消系统粘贴，
///     见 Avalonia PR #6492 的实现），于是这里改为：取消系统粘贴 → 自己读剪贴板 → 清洗 →
///     插入。等价，且同样发生在 <c>MaxLength</c> 生效**之前**。
///   </description></item>
///   <item><description>
///     <b>粘贴含分隔符</b>：同一条清洗规则，逐字符等价于 Windows 的
///     <c>!char.IsWhiteSpace(c) &amp;&amp; c != '-'</c>（空格、制表、换行、<c>-</c> 全去掉）。
///   </description></item>
///   <item><description>
///     <b>只收 8 位</b>：截断在共享的 <see cref="PairingCodeFormatter"/> 里，两端同一份代码。
///   </description></item>
///   <item><description>
///     <b>自动大写</b>：同样来自 <see cref="PairingCodeFormatter"/>（<c>ToUpperInvariant</c>）。
///     Windows 端另外写了 <c>CharacterCasing="Upper"</c>，Avalonia 的 TextBox 没有这个属性
///     （控件与枚举都不存在），所以那一条在 Linux 上由格式化器保证——结果一致，机制不同。
///   </description></item>
///   <item><description>
///     <b>退格跳格 / Delete 跨分隔符</b>：分隔符是格式化产物，不该让用户按两次删除。
///     Windows 在 <c>PreviewKeyDown</c> 里先把选区改成「删掉一个真字符 + 分隔符」；
///     Avalonia 没有 Preview* CLR 事件，本控件改用 <see cref="OnKeyDown"/> 覆写
///     （在 <c>base.OnKeyDown</c> 之前改选区），对裸 TextBox 的挂载路径用
///     <see cref="RoutingStrategies.Tunnel"/> 的 <c>AddHandler</c>——这正是 Avalonia
///     官方迁移文档给出的 <c>PreviewKeyDown</c> 替代写法。
///   </description></item>
///   <item><description>
///     <b>左右移动</b>：Windows **没有**拦截方向键，插入符可以停在分隔符两侧，跨越删除由上面
///     那条规则兜住。这里保持一致，不做「跳过分隔符」的花活。
///   </description></item>
///   <item><description>
///     <b>编辑时机</b>：Windows 的 <c>TextChanged</c> 跑在 WPF 的编辑事务里（最终选区还没落定），
///     所以它必须 <c>Dispatcher.BeginInvoke(Input)</c> 延后到「编辑结束」再重排文本——那段
///     <c>_pending</c> 注释讲的正是这件事。Avalonia 的 <c>TextChanged</c> 本身就是
///     「文本变更并渲染之后」异步触发的，框架已经把这一步做完了，因此这里直接重排即可，
///     不再自己排队；选区与插入符按格式化器算出的位置显式设回。
///     唯一代价：WPF 的 Input 优先级排在渲染之前（中间态不上屏），Avalonia 的异步
///     <c>TextChanged</c> 可能让原始文本上屏一帧。
///   </description></item>
///   <item><description>
///     <b><c>MaxLength</c> 语义</b>：用户手动输入的上限两端一致（9 = 8 位码 + 1 个分隔符）。
///     Avalonia 的 <c>MaxLength</c> 按文档只约束「手动输入」，不约束程序化赋值；可见结果仍由
///     共享格式化器钉死为 <c>XXXX-XXXX</c>。
///   </description></item>
///   <item><description>
///     <b>整体校验</b>：Windows 端**不在控件里**校验，校验在 ViewModel
///     （<c>PairingCodeNormalizer.TryNormalize</c>）。这里同样不校验，只提供
///     <see cref="TryGetCode"/> 这一个便利方法，底层还是那个共享的规范化器。
///   </description></item>
///   <item><description>
///     <b>错误态</b>：Windows 是 <c>Tag="{Binding HasCodeError}"</c> + 样式触发器。
///     Avalonia 的 <c>Control</c> **也有 <c>Tag</c>**（属性在，只是没有触发器机制），
///     所以照搬那个绑定即可：样式侧在 TextBox 的 ControlTheme 里读 Tag（或换成
///     <c>Classes.error</c> 之类的类选择器）。本控件是 TextBox，两种写法都照样成立，
///     因此这里不额外发明「错误态属性」。
///   </description></item>
/// </list>
///
/// <para>
/// 两种用法（二选一，不要同时用）：
/// <code>
/// &lt;!-- 用控件本身（视图里替掉 TextBox） --&gt;
/// &lt;views:PairingCodeInput x:Name="PairingCodeBox"
///                          Text="{Binding PairingCode, UpdateSourceTrigger=PropertyChanged}"
///                          Tag="{Binding HasCodeError}"
///                          MaxLength="9" /&gt;
///
/// &lt;!-- 或保留裸 TextBox，只挂行为（Windows 端的形状） --&gt;
/// &lt;TextBox x:Name="PairingCodeBox" MaxLength="9" /&gt;
/// // code-behind，与 Windows 端那一行逐字相同：
/// _ = new PairingCodeInput(PairingCodeBox);
/// </code>
/// </para>
/// </summary>
public sealed class PairingCodeInput : TextBox
{
    /// <summary>
    /// 共享的编辑行为。只在「用控件本身」时存在；<see cref="PairingCodeInput(TextBox)"/> 那条路
    /// 把行为挂在传进来的 TextBox 上，本实例自身不承载行为。
    /// </summary>
    private readonly PairingCodeEditing? _editing;

    public PairingCodeInput()
    {
        // 与 Windows 端 BindView 的 MaxLength="9" 同值：8 位码 + 1 个分隔符。
        // 默认值只是让控件单用时也安全；视图里照搬写 MaxLength="9" 也一样。
        MaxLength = 9;
        _editing = new PairingCodeEditing(this);
    }

    /// <summary>
    /// Windows 端的写法是 <c>_ = new PairingCodeInput(PairingCodeBox);</c>——把行为装到**已有的**
    /// TextBox 上。这个重载就是为那一行准备的：它让照搬过来的绑定页 code-behind 不必改写，
    /// 代价只是多构造一个不会被显示、也没人引用的控件实例（它的行为不挂在自己身上）。
    /// 等价且更直白的写法是 <see cref="Attach"/>。
    /// </summary>
    public PairingCodeInput(TextBox box)
    {
        Attach(box);
    }

    /// <summary>
    /// 把同一套行为装到任一个 <see cref="TextBox"/> 上（Windows 端 <c>new PairingCodeInput(box)</c>
    /// 的等价物）。
    ///
    /// <para>
    /// 与 Windows 端一样是单向挂载：订阅随 TextBox 一起活到进程结束，没有卸载入口
    /// （WPF 那边同样没有）。因此**不要**对同一个 TextBox 重复调用，也不要对
    /// <see cref="PairingCodeInput"/> 自身调用。
    /// </para>
    /// </summary>
    public static void Attach(TextBox box)
    {
        var editing = new PairingCodeEditing(box);

        // 裸 TextBox 没有可覆写的 OnKeyDown，只能用隧道路由取到「删除之前」这一刻
        // （Avalonia 的 PreviewKeyDown 替代写法，见类注释）。
        box.AddHandler(
            InputElement.KeyDownEvent,
            (_, e) => editing.PrepareDeletion(e.Key),
            RoutingStrategies.Tunnel);
    }

    /// <summary>
    /// 与 Windows 端同名同义：在删除键真正落到文本之前调整选区，让分隔符不占用户一次按键。
    /// 控件的 <see cref="OnKeyDown"/> 会调用它；挂载路径由隧道处理器调用。
    /// </summary>
    public void PrepareDeletion(Key key) => _editing?.PrepareDeletion(key);

    /// <summary>
    /// 整串配对码的校验结果。**校验不在控件里做**（Windows 端也不做）：它属于 ViewModel，
    /// 这里只是把共享的 <see cref="PairingCodeNormalizer"/> 露出来，省得 ViewModel 再摸一次文本。
    /// </summary>
    public bool TryGetCode(out string code) => PairingCodeNormalizer.TryNormalize(Text, out code);

    /// <summary>
    /// WPF <c>TextBox.Select(start, length)</c> 的等价物（Avalonia 的 TextBox 只有 SelectAll）。
    /// 保留这个名字是为了让 Windows 端的行为代码能一行不改地读过来。
    /// </summary>
    public void Select(int start, int length) => PairingCodeEditing.SelectRange(this, start, start + length);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // 必须先改选区，再让 TextBox 处理这次删除——顺序反了删除就已经发生了。
        _editing?.PrepareDeletion(e.Key);
        base.OnKeyDown(e);
    }

    /// <summary>
    /// 一份编辑行为，两种挂载方式共用。它只碰 <see cref="TextBox"/> 的公开表面，
    /// 因此无论 TextBox 是本控件自身还是视图里的一个裸 TextBox，行为都一样。
    /// </summary>
    private sealed class PairingCodeEditing
    {
        private readonly TextBox _box;
        private bool _formatting;

        internal PairingCodeEditing(TextBox box)
        {
            _box = box;
            // Avalonia 的 TextChanged 是「文本已变更并渲染之后」异步触发的——正好是 Windows 端
            // 用 Dispatcher.BeginInvoke 手工等到的那个时刻，所以这里不需要再排一次队。
            box.TextChanged += OnTextChanged;
            // 粘贴：拿不到待粘贴的数据对象，只能取消系统粘贴、自己读剪贴板再清洗（见类注释）。
            box.PastingFromClipboard += OnPastingFromClipboard;
        }

        /// <summary>
        /// 与 Windows 端的 <c>PrepareDeletion</c> 逐行对齐：只有「无选区 + 第 5 个字符是分隔符」
        /// 时才动手，然后把选区扩成「一个真字符 + 那个分隔符」，让一次删除真的删掉一个码位。
        /// </summary>
        internal void PrepareDeletion(Key key)
        {
            string text = _box.Text ?? "";

            // 有选区时用户的意思很明确，不要去改它。
            // 无选区时 SelectionStart == SelectionEnd，它就是插入符位置（Avalonia 的约定）。
            if (_box.SelectionEnd != _box.SelectionStart || text.Length <= 4 || text[4] != '-')
            {
                return;
            }

            int caret = _box.SelectionStart;

            if (key == Key.Back && caret == 5)
            {
                SelectRange(_box, 3, 5);
            }
            else if (key == Key.Delete && caret == 4 && text.Length > 5)
            {
                SelectRange(_box, 4, 6);
            }
        }

        private void OnTextChanged(object? sender, TextChangedEventArgs e)
        {
            // 重排自己会再触发一次变更事件；那一次进来时文本已经是规整的，会直接早退。
            if (_formatting)
            {
                return;
            }

            FormatAfterEdit();
        }

        /// <summary>
        /// Windows 端 <c>FormatAfterEdit</c> 的等价物：按格式化器算出的位置重排文本，并把选区/插入符
        /// 搬回它算出的坐标（分隔符插入会让后面所有下标右移一位，不对齐就会「删掉一个字符却跳两格」）。
        /// </summary>
        private void FormatAfterEdit()
        {
            string original = _box.Text ?? "";
            (string formatted, int start) = PairingCodeFormatter.Format(original, _box.SelectionStart);
            (_, int end) = PairingCodeFormatter.Format(original, _box.SelectionEnd);
            if (string.Equals(original, formatted, StringComparison.Ordinal))
            {
                return;
            }

            _formatting = true;
            try
            {
                // 用 SetCurrentValue 而不是直接赋值：直接赋值会顶掉 XAML 里的 TwoWay 绑定，
                // 之后 ViewModel 再也收不到输入（WPF 端在同一处用 SetCurrentValue 也是这个原因）。
                _box.SetCurrentValue(TextProperty, formatted);
                SelectRange(_box, start, end);
            }
            finally
            {
                _formatting = false;
            }
        }

        private void OnPastingFromClipboard(object? sender, RoutedEventArgs e)
        {
            // 先取消系统粘贴：否则 MaxLength 会先把原始串（可能带空格、换行）截断，
            // 一串合法配对码的尾巴会被吃掉——Windows 端在同一步替换 DataObject，理由相同。
            e.Handled = true;
            _ = PasteSanitizedAsync();
        }

        private async Task PasteSanitizedAsync()
        {
            IClipboard? clipboard = TopLevel.GetTopLevel(_box)?.Clipboard;
            if (clipboard is null)
            {
                return;
            }

            string? text = await clipboard.GetTextAsync();
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            // 与 Windows 端同一条清洗规则：空白与 '-' 先去干净，再交给格式化器统一处理大小写、
            // 截断与分隔符。清洗后长度 ≤ 8，不会再被 MaxLength 咬掉尾巴。
            string compact = string.Concat(text.Where(c => !char.IsWhiteSpace(c) && c != '-'));
            InsertCompact(compact);
            FormatAfterEdit();
        }

        /// <summary>把清洗过的码插到当前插入符处（有选区则替换），随后由格式化器补分隔符。</summary>
        private void InsertCompact(string compact)
        {
            if (compact.Length == 0)
            {
                return;
            }

            string current = _box.Text ?? "";
            int start = Math.Clamp(_box.SelectionStart, 0, current.Length);
            int end = Math.Clamp(_box.SelectionEnd, start, current.Length);
            string next = current[..start] + compact + current[end..];

            _box.SetCurrentValue(TextProperty, next);
            SelectRange(_box, start + compact.Length, start + compact.Length);
        }

        /// <summary>
        /// 设选区（WPF 的 <c>Select(start, length)</c> 形状：这里是 [start, end)）。
        /// 两个属性分两次设，而某些实现会把 <c>SelectionStart</c> 夹进当时的
        /// <c>SelectionEnd</c>；所以设完复核一次，没落上就反序再来一遍。
        /// </summary>
        internal static void SelectRange(TextBox box, int start, int end)
        {
            int length = (box.Text ?? "").Length;
            start = Math.Clamp(start, 0, length);
            end = Math.Clamp(end, start, length);

            box.SelectionStart = start;
            box.SelectionEnd = end;
            if (box.SelectionStart != start || box.SelectionEnd != end)
            {
                box.SelectionEnd = end;
                box.SelectionStart = start;
            }
        }
    }
}
