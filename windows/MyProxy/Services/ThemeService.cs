using System.Windows;
using MyProxy.Core;

namespace MyProxy.Services;

/// <summary>
/// 换肤的实现：把 <c>Application.Resources.MergedDictionaries</c> 的第 0 项整份换掉。
///
/// 为什么是「整份换掉」而不是「叠一层覆盖」——这是这套代码库特有的约束，不是风格选择：
/// DesignTokens 内部有 376 处 <c>{StaticResource}</c>，Style 的模板在字典加载时就把笔刷
/// 解析完并冻结了。再往上叠一个只改 <c>Brush.Accent</c> 的字典，视图直接引用的键会变，
/// 而**模板内部**引用的那些不会变——于是按钮换了色、按钮里的 hover 层没换，
/// 半套新皮肤半套旧皮肤。三份自包含的字典各自内部自洽，没有这个问题。
///
/// 代价是三份字典必须逐键对齐，由 Tests/ThemeParityTests 守住。
/// </summary>
public sealed class ThemeService : IThemeService
{
    private const string DictionaryFolder = "Themes";

    private UiTheme _current = UiTheme.Classic;

    public UiTheme Current => _current;

    public event Action<UiTheme>? ThemeChanged;

    public void Apply(UiTheme theme)
    {
        UiTheme target = UiThemes.Normalize(theme);
        if (target == _current)
        {
            return;
        }

        Application? app = Application.Current;
        if (app is null)
        {
            // 没有 Application 的宿主（单元测试）只记账，不碰资源树。
            _current = target;
            ThemeChanged?.Invoke(target);
            return;
        }

        ResourceDictionary loaded = Load(target);

        var merged = app.Resources.MergedDictionaries;
        if (merged.Count == 0)
        {
            merged.Add(loaded);
        }
        else
        {
            // 就地替换而不是「先删后加」：删掉的一瞬间整棵树会失去所有笔刷，
            // WPF 会把那一帧渲染成无样式的系统默认外观，肉眼可见地闪一下。
            merged[0] = loaded;
        }

        _current = target;
        ThemeChanged?.Invoke(target);
    }

    /// <summary>
    /// 相对 Pack URI，与 App.xaml 里的写法同源。程序集名写死为 MyProxy——
    /// csproj 的 AssemblyName 就是它，改名会在这里立刻炸出来，好过静默回落到默认皮肤。
    /// </summary>
    private static ResourceDictionary Load(UiTheme theme)
    {
        string uri = $"/MyProxy;component/{DictionaryFolder}/{UiThemes.DictionaryFileName(theme)}";
        return (ResourceDictionary)Application.LoadComponent(new Uri(uri, UriKind.Relative));
    }
}
