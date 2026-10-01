namespace MyProxy.Services;

public interface IStartupService
{
    bool IsAutoStartEnabled();
    void SetAutoStartEnabled(bool enabled);

    /// <summary>
    /// 删掉旧实现在 <c>HKCU\...\Run</c> 下留的自启动项。
    ///
    /// <para>
    /// 一次性迁移，每次启动都跑一遍（幂等）。自启动改用计划任务之后，那条 Run
    /// 项在登录时不会静默提权——它要么失败、要么弹 UAC，正是换掉它的原因。
    /// 不清理的话升级上来的用户会永远留着它，而且从应用里关不掉：新的
    /// <see cref="SetAutoStartEnabled"/> 只删计划任务，不认识那个注册表值。
    /// </para>
    /// </summary>
    void RemoveLegacyAutoStart();
}
