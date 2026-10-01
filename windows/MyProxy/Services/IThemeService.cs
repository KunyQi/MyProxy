using MyProxy.Core;

namespace MyProxy.Services;

/// <summary>换肤。唯一允许改 <c>Application.Resources.MergedDictionaries</c> 的地方。</summary>
public interface IThemeService
{
    /// <summary>当前生效的皮肤。</summary>
    UiTheme Current { get; }

    /// <summary>
    /// 把皮肤换成 <paramref name="theme"/>。同一套皮肤重复调用是空操作。
    /// 失败时保持原皮肤不变并抛出——调用方要能把设置项回滚到用户看得见的真实状态。
    /// </summary>
    void Apply(UiTheme theme);

    /// <summary>
    /// 皮肤换完之后触发。视图全部用 <c>{StaticResource}</c> 取值，换字典不会自动重绘，
    /// 必须由订阅者（MainWindow）把当前页重新实例化一次。
    /// </summary>
    event Action<UiTheme>? ThemeChanged;
}
