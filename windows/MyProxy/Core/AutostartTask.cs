using System.IO;
using System.Xml;
using System.Xml.Linq;

namespace MyProxy.Core;

/// <summary>
/// 开机自启计划任务的定义。纯逻辑，无 I/O：生成交给任务计划程序 <c>RegisterTask</c> 的文档，
/// 以及判定一份导出的任务是不是指向当前 exe、且是当前这一版的设置。
///
/// <para>
/// 不再用 <c>schtasks /SC ONLOGON /RL HIGHEST</c> 的命令行形式：它只能设触发器与权限，
/// 其余沿用任务计划程序的默认值——电池供电时不启动、切到电池即终止、连续运行 72 小时后
/// 强制结束、以低于正常的优先级（7）运行。对一个常驻托盘、系统代理指向它的本地端口的
/// 程序，被终止就是把代理留在一个死端口上。
/// </para>
/// </summary>
public static class AutostartTask
{
    public const string Argument = "--autostart";

    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    /// <summary>
    /// 只在 <paramref name="userSid"/> 这个用户登录时、以最高可用权限、在其交互会话里运行。
    /// </summary>
    public static string BuildXml(string executablePath, string userSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(userSid);
        if (!Path.IsPathFullyQualified(executablePath))
        {
            throw new ArgumentException("自启动任务必须指向绝对路径。", nameof(executablePath));
        }

        var task = new XElement(Ns + "Task",
            new XAttribute("version", "1.2"),
            new XElement(Ns + "RegistrationInfo",
                new XElement(Ns + "Description", "客户端自动启动")),
            new XElement(Ns + "Triggers",
                new XElement(Ns + "LogonTrigger",
                    new XElement(Ns + "Enabled", "true"),
                    new XElement(Ns + "UserId", userSid))),
            new XElement(Ns + "Principals",
                new XElement(Ns + "Principal",
                    new XAttribute("id", "Author"),
                    new XElement(Ns + "UserId", userSid),
                    new XElement(Ns + "LogonType", "InteractiveToken"),
                    // 登录时无提示提权：这正是换掉 HKCU\Run 的原因。
                    new XElement(Ns + "RunLevel", "HighestAvailable"))),
            new XElement(Ns + "Settings",
                // 已在运行（用户手动开过）就不再拉第二个；单实例锁也会挡，但不必走到那一步。
                new XElement(Ns + "MultipleInstancesPolicy", "IgnoreNew"),
                new XElement(Ns + "DisallowStartIfOnBatteries", "false"),
                new XElement(Ns + "StopIfGoingOnBatteries", "false"),
                new XElement(Ns + "AllowHardTerminate", "true"),
                new XElement(Ns + "StartWhenAvailable", "false"),
                new XElement(Ns + "RunOnlyIfNetworkAvailable", "false"),
                new XElement(Ns + "IdleSettings",
                    new XElement(Ns + "StopOnIdleEnd", "false"),
                    new XElement(Ns + "RestartOnIdle", "false")),
                new XElement(Ns + "AllowStartOnDemand", "true"),
                new XElement(Ns + "Enabled", "true"),
                new XElement(Ns + "Hidden", "false"),
                new XElement(Ns + "RunOnlyIfIdle", "false"),
                new XElement(Ns + "WakeToRun", "false"),
                // PT0S = 不限时长。默认的 PT72H 会在连续运行三天后结束进程。
                new XElement(Ns + "ExecutionTimeLimit", "PT0S"),
                // 4 = 正常优先级。默认的 7 连同它拉起的 xray 一起被降级。
                new XElement(Ns + "Priority", "4")),
            new XElement(Ns + "Actions",
                new XAttribute("Context", "Author"),
                new XElement(Ns + "Exec",
                    new XElement(Ns + "Command", executablePath),
                    new XElement(Ns + "Arguments", Argument))));

        // 以字符串（BSTR）交给 COM，不落盘；UTF-16 声明与之一致。
        return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>" + Environment.NewLine + task;
    }

    /// <summary>
    /// 一份 <c>schtasks /Query /XML</c> 导出的任务是否指向 <paramref name="executablePath"/>，
    /// 并且带着当前这一版的设置。任何一条不满足都视为「未启用」，由启动逻辑重建——
    /// 更新换过安装目录的、旧版按默认值建的，都走这条路迁移过来。
    /// </summary>
    public static bool Matches(string exportedXml, string executablePath)
    {
        XElement? task = TryParse(exportedXml);
        if (task is null)
        {
            return false;
        }

        string? command = task.Element(Ns + "Actions")?.Element(Ns + "Exec")?.Element(Ns + "Command")?.Value;
        if (command is null ||
            !string.Equals(command.Trim().Trim('"'), executablePath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        XElement? settings = task.Element(Ns + "Settings");
        XElement? principal = task.Element(Ns + "Principals")?.Element(Ns + "Principal");
        return settings is not null
            && principal is not null
            && task.Element(Ns + "Triggers")?.Element(Ns + "LogonTrigger") is not null
            && Is(principal, "RunLevel", "HighestAvailable")
            && Is(settings, "DisallowStartIfOnBatteries", "false")
            && Is(settings, "StopIfGoingOnBatteries", "false")
            && Is(settings, "ExecutionTimeLimit", "PT0S")
            && Is(settings, "Priority", "4");
    }

    private static bool Is(XElement parent, string name, string expected)
        => string.Equals(parent.Element(Ns + name)?.Value.Trim(), expected, StringComparison.OrdinalIgnoreCase);

    private static XElement? TryParse(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return null;
        }

        try
        {
            // 导出的文本已经解码成字符串，里面的 encoding="UTF-16" 声明只是字面量。
            XElement root = XDocument.Parse(xml.Trim()).Root!;
            return root.Name == Ns + "Task" ? root : null;
        }
        catch (XmlException)
        {
            return null;
        }
    }
}
