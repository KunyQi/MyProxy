namespace MyProxy.Core;

public static class ErrorCodeMessages
{
    public const string ProxyRestoreFailed = "系统代理恢复失败，请重启应用";

    /// <summary>
    /// 已连接、但心跳因服务器证书校验失败而失败时，主页状态行的文案。与 ServerUntrusted
    /// 同义，只是那一行是不换行的保留行，放不下完整的那句。
    /// </summary>
    public const string ServerIdentityUnverifiedWhileConnected = "无法验证服务器身份，请联系管理员";

    public static string Get(ErrorCode errorCode)
    {
        return errorCode switch
        {
            ErrorCode.NotBound => "设备尚未绑定，请先完成绑定",
            ErrorCode.ApiUnreachable => "暂时无法连接服务器，请检查网络",
            ErrorCode.ServerNotConfigured => "尚未配置服务器，请联系部署管理员。",
            ErrorCode.NoValidConfig => "暂无可用配置，请重新绑定",
            ErrorCode.PairingInvalid => "配对码无效，请检查后重试",
            ErrorCode.PairingExpired => "配对码已过期，请获取新的配对码",
            ErrorCode.TokenInvalid => "设备已失效，请重新绑定",
            ErrorCode.ServerUntrusted => "无法验证服务器身份，请联系部署管理员",
            ErrorCode.XrayMissing => "程序组件缺失，请重新下载",
            ErrorCode.XrayStartFailed => "启动失败，请查看日志",
            ErrorCode.PortUnavailable => "本地端口被占用，请稍后重试",
            ErrorCode.ConnectTestFailed => "无法连接服务器，请稍后重试",
            ErrorCode.ProxyApplyFailed => "系统代理设置失败，请重试",
            _ => "发生未知错误，请查看日志"
        };
    }
}
