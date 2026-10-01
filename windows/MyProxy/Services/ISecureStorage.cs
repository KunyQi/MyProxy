namespace MyProxy.Services;

public interface ISecureStorage
{
    Task SaveAsync(string name, string plainText, CancellationToken ct);
    Task<string?> ReadAsync(string name, CancellationToken ct);
    Task DeleteAsync(string name, CancellationToken ct);
}
