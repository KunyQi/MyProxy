namespace MyProxy.Services;

/// <summary>The platform rejected the server chain, validity or hostname.</summary>
public sealed class ServerCertificateRejectedException : Exception
{
    public ServerCertificateRejectedException()
        : base("The server certificate chain, validity or hostname is not trusted.") { }
}

/// <summary>Keep certificate identity failures distinct from interrupted TLS handshakes.</summary>
public static class TlsFailure
{
    public static bool IsServerCertificateRejected(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Exception? current = exception;
        for (int depth = 0; current is not null && depth < 8; depth++)
        {
            if (current is ServerCertificateRejectedException) return true;
            current = current.InnerException;
        }
        return false;
    }
}
