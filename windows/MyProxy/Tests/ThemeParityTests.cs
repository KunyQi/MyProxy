using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MyProxy.Core;

namespace MyProxy.Tests;

/// <summary>
/// 三套皮肤的契约守卫。
///
/// 换肤这件事在这套代码库里有一个天然的失败模式：视图取样式全部走
/// <c>{StaticResource}</c>，键找不到时 **XAML 编译照样通过**，只在运行到那一页、
/// 那一个状态时才炸 —— 而且往往是某个不常见的状态（比如「已连接且自检失败」）。
/// 靠手点是点不出来的。
///
/// 所以这里按文本静态核验，不依赖跑起来：
///   1. 视图层引用的每一个键，三套皮肤都必须提供；
///   2. 每份字典**内部**的引用也必须在同一份字典里定义
///      （写这套皮肤时真的踩过：釉陶引用了从来不存在的 Brush.SuccessDeep，
///        编译通过、渲染到「已连接」时才抛 UnsetValue）；
///   3. 枚举里的每一档都得有对应的文件。
/// </summary>
[TestClass]
public sealed class ThemeParityTests
{
    private static readonly Regex KeyPattern = new(@"x:Key=""([^""]+)""", RegexOptions.Compiled);
    private static readonly Regex StaticRefPattern =
        new(@"\{StaticResource\s+([^}]+?)\s*\}", RegexOptions.Compiled);
    private static readonly Regex AnyRefPattern =
        new(@"\{(?:Static|Dynamic)Resource\s+([^}]+?)\s*\}", RegexOptions.Compiled);

    private static string ProjectRoot => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static string ThemePath(UiTheme theme)
        => Path.Combine(ProjectRoot, "Themes", UiThemes.DictionaryFileName(theme));

    private static HashSet<string> KeysOf(string xaml)
        => KeyPattern.Matches(xaml).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

    [TestMethod]
    public void EveryThemeFileExists()
    {
        foreach (UiTheme theme in UiThemes.All)
        {
            string path = ThemePath(theme);
            Assert.IsTrue(File.Exists(path),
                $"{theme} 指向的字典不存在：{path}。UiThemes.DictionaryFileName 与磁盘对不上。");
            Assert.IsFalse(string.IsNullOrWhiteSpace(UiThemes.DisplayName(theme)),
                $"{theme} 没有界面名字，设置页的分段控件会出现一个空格子。");
        }
    }

    /// <summary>视图层（App / MainWindow / Views）引用的键，三套皮肤都得有。</summary>
    [TestMethod]
    public void EveryThemeCoversEveryKeyTheViewsAskFor()
    {
        var viewFiles = new List<string>
        {
            Path.Combine(ProjectRoot, "App.xaml"),
            Path.Combine(ProjectRoot, "MainWindow.xaml")
        };
        viewFiles.AddRange(Directory.EnumerateFiles(Path.Combine(ProjectRoot, "Views"), "*.xaml"));

        var wanted = new HashSet<string>(StringComparer.Ordinal);
        foreach (string file in viewFiles)
        {
            foreach (Match m in AnyRefPattern.Matches(File.ReadAllText(file)))
            {
                wanted.Add(m.Groups[1].Value);
            }
        }

        Assert.IsTrue(wanted.Count > 40, $"只从视图里解析出 {wanted.Count} 个键，解析大概是坏的。");

        foreach (UiTheme theme in UiThemes.All)
        {
            HashSet<string> have = KeysOf(File.ReadAllText(ThemePath(theme)));
            string[] missing = wanted.Where(k => !have.Contains(k)).OrderBy(k => k).ToArray();
            Assert.AreEqual(0, missing.Length,
                $"{theme} 缺少视图要用的键：{string.Join(", ", missing)}。"
                + "换到这套皮肤后，用到这些键的页面会在构造时抛 ResourceReferenceKeyNotFoundException。");
        }
    }

    /// <summary>每份字典内部的 StaticResource 引用，必须在同一份字典里定义。</summary>
    [TestMethod]
    public void EveryThemeResolvesItsOwnReferences()
    {
        foreach (UiTheme theme in UiThemes.All)
        {
            string xaml = File.ReadAllText(ThemePath(theme));
            HashSet<string> defined = KeysOf(xaml);
            string[] dangling = StaticRefPattern.Matches(xaml)
                .Select(m => m.Groups[1].Value)
                .Distinct(StringComparer.Ordinal)
                .Where(k => !defined.Contains(k))
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToArray();

            Assert.AreEqual(0, dangling.Length,
                $"{theme} 里有 {dangling.Length} 个引用在本字典中没有定义：{string.Join(", ", dangling)}。"
                + "字典是自包含的，找不到的键不会编译失败，只会在渲染到那一处时变成 UnsetValue。");
        }
    }

    /// <summary>
    /// 三套皮肤面向视图的键集必须逐键相同。皮肤自己的私有键（釉陶的 Glaze.* 一类）
    /// 允许存在，但不能出现「这套有、那套没有」的**公共**键。
    /// </summary>
    [TestMethod]
    public void ThemesAgreeOnTheSharedKeySet()
    {
        HashSet<string> classic = KeysOf(File.ReadAllText(ThemePath(UiTheme.Classic)));

        foreach (UiTheme theme in UiThemes.All.Where(t => t != UiTheme.Classic))
        {
            HashSet<string> other = KeysOf(File.ReadAllText(ThemePath(theme)));
            string[] missing = classic.Where(k => !other.Contains(k)).OrderBy(k => k).ToArray();

            Assert.AreEqual(0, missing.Length,
                $"{theme} 少了出厂皮肤有的键：{string.Join(", ", missing)}。"
                + "三套必须对得上，否则视图只在某一套皮肤下能用。");
        }
    }

    /// <summary>字典真的能被 WPF 加载，而不只是文本长得对。</summary>
    [TestMethod]
    public void EveryThemeLoadsAndPaintsTheEssentials() => IconGeometryTests.RunSta(() =>
    {
        foreach (UiTheme theme in UiThemes.All)
        {
            var dict = (ResourceDictionary)Application.LoadComponent(new Uri(
                $"/MyProxy;component/Themes/{UiThemes.DictionaryFileName(theme)}", UriKind.Relative));

            foreach (string key in new[]
                     {
                         "Brush.Background", "Brush.Surface", "Brush.TextPrimary",
                         "Brush.Orb.Idle", "OrbButtonStyle", "ToggleSwitchStyle",
                         "SegmentedItemLeftStyle", "SegmentedItemMiddleStyle", "SegmentedItemRightStyle"
                     })
            {
                Assert.IsTrue(dict.Contains(key), $"{theme} 加载后取不到 {key}");
                Assert.IsNotNull(dict[key], $"{theme} 的 {key} 解析成了 null");
            }
        }
    });
}
