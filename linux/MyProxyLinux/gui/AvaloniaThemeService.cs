using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using MyProxy.Core;

namespace MyProxy.Gui;

/// <summary>
/// 换肤。**全进程唯一允许改 <c>Application.Resources.MergedDictionaries</c> 的地方**，
/// 与 Windows 端 <c>ThemeService</c> 的定位完全一致：它替换的是第 0 项，也就是
/// 「皮肤入口」那一份字典（<c>Themes/DesignTokens.axaml</c>、<c>Ceramic.axaml</c>、
/// <c>Porcelain.axaml</c>）。
///
/// <para>
/// 三套皮肤同骨不同皮：样式文件是共用的同一批，只有令牌不同。因此换肤只是换一份令牌。
/// </para>
///
/// <para>
/// 与 Windows 端的一处不同：那边视图全部用 <c>{StaticResource}</c>，换完字典已解析的
/// 值不会重取，所以它必须在换肤后把当前页<b>重建</b>一次；这边样式与视图一律用
/// <c>{DynamicResource}</c>，换完即生效，不必重建——ViewMode 状态因此也不会被扰动。
/// </para>
/// </summary>
public static class AvaloniaThemeService
{
    private const string DefaultSkin = "Themes/DesignTokens.axaml";

    /// <summary>当前皮肤，便于「换到同一套」时不重复加载。</summary>
    public static UiTheme Current { get; private set; } = UiTheme.Classic;

    /// <summary>把界面换成指定皮肤。失败时保持原样并返回 false（不抛给调用方）。</summary>
    public static bool Apply(UiTheme theme)
    {
        if (theme == Current)
        {
            return true;
        }

        string source = theme switch
        {
            UiTheme.Ceramic => "Themes/Ceramic.axaml",
            UiTheme.Porcelain => "Themes/Porcelain.axaml",
            _ => DefaultSkin
        };

        if (Application.Current is not Application app)
        {
            return false;
        }

        try
        {
            var dictionary = (ResourceDictionary)AvaloniaXamlLoader.Load(
                new Uri($"avares://myproxy-gui/{source}"));

            var merged = app.Resources.MergedDictionaries;
            if (merged.Count == 0)
            {
                merged.Add(dictionary);
            }
            else
            {
                // 只换第 0 项：它是皮肤入口，其余合并字典（如果有）不属于皮肤。
                merged[0] = dictionary;
            }

            Current = theme;
            return true;
        }
        catch (Exception)
        {
            // 换肤失败不是致命错误：界面留在当前皮肤上，功能一点不受影响。
            return false;
        }
    }
}
