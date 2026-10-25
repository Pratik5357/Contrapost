namespace Contrapost.Application.Clients;

public static class Scopes
{
    public const string AccountsRead = "accounts:read";
    public const string AccountsWrite = "accounts:write";
    public const string TransfersWrite = "transfers:write";
    public const string HoldsWrite = "holds:write";
    public const string AdminRead = "admin:read";

    public static readonly IReadOnlyList<string> All = [AccountsRead, AccountsWrite, TransfersWrite, HoldsWrite, AdminRead];
}

/// <summary>What authentication needs to know about a key's client.</summary>
public sealed record ApiClientCredentials(Guid ClientId, byte[] KeyHash, IReadOnlyList<string> Scopes);

public interface IApiClientStore
{
    /// <summary>The active (not revoked) client with this key prefix, or null.</summary>
    Task<ApiClientCredentials?> FindByPrefixAsync(string prefix, CancellationToken cancellationToken);

    Task CreateAsync(Guid clientId, string name, ApiKeys.Generated key, IReadOnlyList<string> scopes, CancellationToken cancellationToken);
}
