namespace MyProxy.Services;

/// <summary>
/// 安全存储中的文件存在但读不出来。
/// 与「文件不存在」（返回 null）严格区分：把两者都当成「没有绑定」会让一次瞬时
/// I/O 故障覆盖掉仍然有效的凭据。
/// </summary>
public sealed class SecureStorageReadException : Exception
{
    public SecureStorageReadException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
