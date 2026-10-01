using MyProxy.Models;

namespace MyProxy.Services;

/// <summary>Send the pairing code to the configured server over a verified connection.</summary>
public interface IBindingService
{
    Task<BindResult> BindAsync(string pairingCode, CancellationToken ct);
}
