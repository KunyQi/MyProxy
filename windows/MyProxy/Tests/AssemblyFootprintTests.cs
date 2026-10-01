using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MyProxy.Services;

namespace MyProxy.Tests;

/// <summary>
/// 产品程序集的依赖足迹。自包含发布把整个 Microsoft.WindowsDesktop.App 打进来，
/// 里面 WPF 与 WinForms 并存；csproj 有一个 target 从发布清单里剔除 WinForms，
/// 而那件事只有在**代码真的不引用它**时才安全。这条测试守的就是那个前提。
/// </summary>
[TestClass]
public sealed class AssemblyFootprintTests
{
    [TestMethod]
    public void ProductAssembly_DoesNotReferenceWindowsForms()
    {
        AssemblyName[] references = typeof(TrayService).Assembly.GetReferencedAssemblies();

        string[] winforms = references
            .Select(r => r.Name ?? "")
            .Where(n => n.StartsWith("System.Windows.Forms", StringComparison.Ordinal))
            .ToArray();

        Assert.AreEqual(0, winforms.Length,
            "引用了 WinForms：csproj 的 TrimWindowsFormsFromPublish 会把这些程序集从发布里删掉，"
            + "留着引用等于让发布出来的包在运行时炸 FileNotFoundException。引用者："
            + string.Join(", ", winforms));
    }

    /// <summary>
    /// 与 MyProxy.csproj 的 MyProxyUnusedRuntimeFiles 同一份清单（托管的那部分）。
    /// 这些程序集被从发布里剔除，前提是没有任何代码路径会加载它们；产品程序集直接引用
    /// 其中任何一个，前提就不成立了——要么别引用，要么先把它从 csproj 的清单里拿掉。
    /// </summary>
    private static readonly string[] ExcludedFromPublish =
    {
        "System.Windows.Controls.Ribbon",
        "UIAutomationClient",
        "UIAutomationClientSideProviders",
        "PresentationFramework.Aero",
        "PresentationFramework.Luna",
        "PresentationFramework.Royale",
        "WindowsFormsIntegration",
        "System.Design",
        "System.Drawing.Design",
        "Microsoft.Win32.Registry.AccessControl",
        "System.Diagnostics.EventLog.Messages",
    };

    [TestMethod]
    public void ProductAssembly_DoesNotReferenceAssembliesTrimmedFromPublish()
    {
        AssemblyName[] references = typeof(TrayService).Assembly.GetReferencedAssemblies();

        string[] offending = references
            .Select(r => r.Name ?? "")
            .Where(name => ExcludedFromPublish.Contains(name, StringComparer.Ordinal))
            .ToArray();

        Assert.AreEqual(0, offending.Length,
            "产品程序集引用了已从发布中剔除的程序集，发布出来的包会在运行时 FileNotFoundException："
            + string.Join(", ", offending));
    }

    [TestMethod]
    public void TrimList_InTheTestMatchesTheProjectFile()
    {
        // 两份清单漂移了，上面那条测试就守不住 csproj 真正剔除的东西。
        string csproj = File.ReadAllText(Path.Combine(FindWindowsProjectDirectory(), "MyProxy.csproj"));
        int start = csproj.IndexOf("<MyProxyUnusedRuntimeFiles>", StringComparison.Ordinal);
        int end = csproj.IndexOf("</MyProxyUnusedRuntimeFiles>", StringComparison.Ordinal);
        Assert.IsTrue(start >= 0 && end > start, "MyProxy.csproj 里找不到 MyProxyUnusedRuntimeFiles");

        string[] listed = csproj[(start + "<MyProxyUnusedRuntimeFiles>".Length)..end]
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (string name in ExcludedFromPublish)
        {
            CollectionAssert.Contains(listed, name, $"测试清单里的 {name} 不在 csproj 的剔除清单里");
        }
    }

    private static string FindWindowsProjectDirectory()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MyProxy.csproj")))
            {
                return directory.FullName;
            }
        }

        throw new AssertInconclusiveException("测试目录之上找不到 MyProxy.csproj");
    }

    [TestMethod]
    public void ProductAssembly_DoesNotReferenceSystemDrawingCommon()
    {
        // System.Drawing.Common 是 WinForms 那条线上的老依赖，一并守住：
        // 它在非 Windows 上会抛，且托盘图标现在走 ExtractIconEx，不需要它。
        AssemblyName[] references = typeof(TrayService).Assembly.GetReferencedAssemblies();

        Assert.IsFalse(
            references.Any(r => r.Name == "System.Drawing.Common"),
            "不该再引用 System.Drawing.Common");
    }
}
