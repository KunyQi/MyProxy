using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MyProxy.Core;

namespace MyProxy.Tests;

/// <summary>
/// 自启任务的定义。创建与查询本身要管理员权限，由 Step12_13Tests 在提权的 CI runner 上
/// 真跑 schtasks；这里只钉住文档内容与「是否需要重建」的判定。
/// </summary>
[TestClass]
public sealed class AutostartTaskTests
{
    private const string Exe = @"C:\Program Files\MyProxy & Co\MyProxy.exe";
    private const string Sid = "S-1-5-21-1111111111-2222222222-3333333333-1001";
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    [TestMethod]
    public void Settings_KeepTheAppRunningOnBatteryAndWithoutATimeLimit()
    {
        XElement settings = Parse(AutostartTask.BuildXml(Exe, Sid)).Element(Ns + "Settings")!;

        // 这四项的默认值分别是：电池上不启动、切到电池即终止、72 小时后强制结束、优先级 7。
        Assert.AreEqual("false", settings.Element(Ns + "DisallowStartIfOnBatteries")!.Value);
        Assert.AreEqual("false", settings.Element(Ns + "StopIfGoingOnBatteries")!.Value);
        Assert.AreEqual("PT0S", settings.Element(Ns + "ExecutionTimeLimit")!.Value);
        Assert.AreEqual("4", settings.Element(Ns + "Priority")!.Value);
        Assert.AreEqual("IgnoreNew", settings.Element(Ns + "MultipleInstancesPolicy")!.Value);
    }

    [TestMethod]
    public void RunsElevated_OnlyWhenThisUserLogsOn()
    {
        XElement task = Parse(AutostartTask.BuildXml(Exe, Sid));

        XElement principal = task.Element(Ns + "Principals")!.Element(Ns + "Principal")!;
        Assert.AreEqual("HighestAvailable", principal.Element(Ns + "RunLevel")!.Value);
        Assert.AreEqual("InteractiveToken", principal.Element(Ns + "LogonType")!.Value);
        Assert.AreEqual(Sid, principal.Element(Ns + "UserId")!.Value);
        Assert.AreEqual(
            Sid,
            task.Element(Ns + "Triggers")!.Element(Ns + "LogonTrigger")!.Element(Ns + "UserId")!.Value,
            "不带 UserId 的登录触发器是「任何用户登录时」");
    }

    [TestMethod]
    public void Action_IsTheExactExecutable_EvenWithCharactersXmlMustEscape()
    {
        XElement exec = Parse(AutostartTask.BuildXml(Exe, Sid))
            .Element(Ns + "Actions")!.Element(Ns + "Exec")!;

        Assert.AreEqual(Exe, exec.Element(Ns + "Command")!.Value);
        Assert.AreEqual("--autostart", exec.Element(Ns + "Arguments")!.Value);
    }

    [TestMethod]
    public void BuiltTask_Matches_ItsOwnExecutableOnly()
    {
        string xml = AutostartTask.BuildXml(Exe, Sid);

        Assert.IsTrue(AutostartTask.Matches(xml, Exe));
        Assert.IsTrue(AutostartTask.Matches(xml, Exe.ToUpperInvariant()), "Windows 路径不区分大小写");
        Assert.IsFalse(AutostartTask.Matches(xml, @"C:\Old\MyProxy.exe"), "更新换过目录后必须重建");
    }

    [TestMethod]
    public void TaskWithSchedulerDefaults_IsNotAMatch_SoItGetsRebuilt()
    {
        // 旧版 schtasks /SC ONLOGON /RL HIGHEST 建出来、再用 /Query /XML 导出的样子。
        string legacy = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Triggers><LogonTrigger><Enabled>true</Enabled></LogonTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>{Sid}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>true</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>true</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT72H</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author"><Exec><Command>"{Exe.Replace("&", "&amp;")}"</Command><Arguments>--autostart</Arguments></Exec></Actions>
            </Task>
            """;

        Assert.IsFalse(AutostartTask.Matches(legacy, Exe));
    }

    [DataTestMethod]
    [DataRow("")]
    [DataRow("ERROR: The system cannot find the file specified.")]
    [DataRow("<NotATask/>")]
    public void UnreadableOutput_IsNotAMatch(string output)
    {
        Assert.IsFalse(AutostartTask.Matches(output, Exe));
    }

    [TestMethod]
    public void RelativeOrMissingInputs_AreRejected()
    {
        Assert.ThrowsException<ArgumentException>(() => AutostartTask.BuildXml(@"MyProxy.exe", Sid));
        Assert.ThrowsException<ArgumentException>(() => AutostartTask.BuildXml(Exe, ""));
    }

    private static XElement Parse(string xml) => XDocument.Parse(xml).Root!;
}
