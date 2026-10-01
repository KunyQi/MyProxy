using System.IO;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MyProxy.Tests;

[TestClass]
public sealed class PorcelainSurfaceTests
{
    /// <summary>Background / Fill 被赋成纯白的三种写法。</summary>
    private static readonly Regex PureWhiteSurface = new(
        @"(?:Background|Fill)\s*=\s*""\s*(?:White|#FFFFFF|#FFF)\s*""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Setter 形式：Property="Background" ... Value="White"。</summary>
    private static readonly Regex PureWhiteSetter = new(
        @"Property\s*=\s*""(?:Background|Fill)""\s+Value\s*=\s*""\s*(?:White|#FFFFFF|#FFF)\s*""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [TestMethod]
    public void NoSurfaceIsPaintedPureWhite()
    {
        foreach (string path in XamlFiles())
        {
            string xaml = File.ReadAllText(path);
            foreach (Regex pattern in new[] { PureWhiteSurface, PureWhiteSetter })
            {
                Match hit = pattern.Match(xaml);
                Assert.IsFalse(
                    hit.Success,
                    $"{Path.GetFileName(path)} 把一块面刷成了纯白：「{hit.Value}」。"
                    + "实体面一律用 Brush.Surface(#FCFCFA)；纯白只许出现在文字与带 alpha 的高光里"
                    + "。");
            }
        }
    }

    private static IEnumerable<string> XamlFiles()
    {
        // 测试运行目录在 Tests/bin/<cfg>/<tfm>，往上四级回到项目根。
        string root = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", ".."));

        string themes = Path.Combine(root, "Themes");
        string views = Path.Combine(root, "Views");
        Assert.IsTrue(Directory.Exists(themes), $"找不到 Themes 目录：{themes}");
        Assert.IsTrue(Directory.Exists(views), $"找不到 Views 目录：{views}");

        // 项目根的 App.xaml / MainWindow.xaml 也要扫：窗口根元素刷成纯白，
        // 每一页都会坐在纯白上，而只扫 Themes/Views 的守卫会报绿。
        return Directory.EnumerateFiles(root, "*.xaml")
            .Concat(Directory.EnumerateFiles(themes, "*.xaml"))
            .Concat(Directory.EnumerateFiles(views, "*.xaml"));
    }
}
