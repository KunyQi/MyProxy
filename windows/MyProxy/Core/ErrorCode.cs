namespace MyProxy.Core;

public enum ErrorCode
{
    NotBound,
    ApiUnreachable,
    ServerNotConfigured,
    NoValidConfig,
    PairingInvalid,
    PairingExpired,
    TokenInvalid,
    ServerUntrusted,
    XrayMissing,
    XrayStartFailed,
    PortUnavailable,
    ConnectTestFailed,
    ProxyApplyFailed,
    Unknown
}
