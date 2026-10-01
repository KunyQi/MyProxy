using System.IO;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MyProxy.Core;
using MyProxy.Models;

namespace MyProxy.Tests;

/// <summary>
/// 用户可见的模式名称，以及「改名不动语义」这条边界。
/// </summary>
[TestClass]
public sealed class ProxyModeTextTests
{
    [TestMethod]
    public void NamesSayHowTrafficFlows_NotThatRulesAreEditable()
    {
        // 「规则」听起来像有一个用户能增删改的规则集，而分流策略是内置且固定的。
        Assert.AreEqual("智能分流", ProxyModeText.NameFor(ProxyMode.Rule));
        Assert.AreEqual("全局代理", ProxyModeText.NameFor(ProxyMode.Global));
    }

    [TestMethod]
    public void BothDescriptionsMentionThatBlockingNeverStops()
    {
        // 只在一种模式下提「私网与 BT 被阻断」，会让人以为换个模式就不拦了。
        foreach (ProxyMode mode in new[] { ProxyMode.Rule, ProxyMode.Global })
        {
            string description = ProxyModeText.DescriptionFor(mode);
            Assert.IsTrue(description.Contains("私网"), mode.ToString());
            Assert.IsTrue(description.Contains("BT"), mode.ToString());
        }
    }

    [TestMethod]
    public void EnumAndStoredValuesAreUnchanged()
    {
        // 改名只动文案。枚举名与序号是存储值与协议字段的基础，动了就要迁移数据。
        Assert.AreEqual(0, (int)ProxyMode.Rule);
        Assert.AreEqual(1, (int)ProxyMode.Global);
        Assert.AreEqual("Rule", ProxyMode.Rule.ToString());
        Assert.AreEqual("Global", ProxyMode.Global.ToString());
    }

    [TestMethod]
    public void SettingsRoundTripsTheStoredName()
    {
        // 设置文件里存的是枚举名。用户看到的字变了，磁盘上的字不能变，
        // 否则老配置升级后会读不出模式。
        var settings = new AppSettings { ProxyMode = ProxyMode.Global };
        string json = System.Text.Json.JsonSerializer.Serialize(settings);

        Assert.IsTrue(json.Contains("Global") || json.Contains("\"proxyMode\":1"), json);
        Assert.IsFalse(json.Contains("全局代理"), "用户文案不得写进存储");
    }

    /// <summary>
    /// 三份皮肤的模式开关必须说同一句话。
    ///
    /// 它们是三份独立的 XAML，改名时漏掉一份不会让编译失败，只会让换了皮肤的
    /// 用户看到旧名字——这正是需要一条断言而不是靠记性的地方。
    /// </summary>
    [DataTestMethod]
    [DataRow("Ceramic")]
    [DataRow("DesignTokens")]
    [DataRow("Porcelain")]
    public void EveryThemeUsesTheSameModeLabels(string theme)
    {
        string path = Path.Combine(RepositoryRoot(), "windows", "MyProxy", "Themes", $"{theme}.xaml");
        Assert.IsTrue(File.Exists(path), path);
        string markup = File.ReadAllText(path);

        StringAssert.Contains(markup, $"Content=\"{ProxyModeText.RuleName}\"");
        StringAssert.Contains(markup, $"Content=\"{ProxyModeText.GlobalName}\"");

        // 旧名字不得残留。用 Content="规则" 这种完整形式匹配，避免误伤注释里
        // 正常出现的「规则」二字（路由规则、排布规则等）。
        Assert.IsFalse(Regex.IsMatch(markup, "Content=\"规则\""), theme);
        Assert.IsFalse(Regex.IsMatch(markup, "Content=\"全局\""), theme);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(System.AppContext.BaseDirectory);
        // git worktree 里的 .git 是一个指向主仓库的文件，不是目录。
        while (directory is not null
            && !Directory.Exists(Path.Combine(directory.FullName, ".git"))
            && !File.Exists(Path.Combine(directory.FullName, ".git")))
        {
            directory = directory.Parent;
        }

        Assert.IsNotNull(directory, "未找到仓库根目录");
        return directory!.FullName;
    }
}
