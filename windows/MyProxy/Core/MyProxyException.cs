namespace MyProxy.Core;

public sealed class MyProxyException : Exception
{
    public ErrorCode ErrorCode { get; }

    public string FriendlyMessage { get; }

    public MyProxyException(string friendlyMessage, ErrorCode errorCode)
        : base(friendlyMessage)
    {
        FriendlyMessage = friendlyMessage;
        ErrorCode = errorCode;
    }

    public MyProxyException(string friendlyMessage, ErrorCode errorCode, Exception innerException)
        : base(friendlyMessage, innerException)
    {
        FriendlyMessage = friendlyMessage;
        ErrorCode = errorCode;
    }
}
