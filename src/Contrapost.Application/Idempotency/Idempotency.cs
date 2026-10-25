using System.Security.Cryptography;
using System.Text;

namespace Contrapost.Application.Idempotency;

public sealed record IdempotencyKey(Guid ClientId, string Key, string Method, string Path, byte[] RequestHash)
{
    public const int MaxLength = 255;

    /// <summary>SHA-256 of <c>method + path + canonical JSON body</c> (design doc §7.3).</summary>
    public static byte[] HashRequest(string method, string path, string canonicalBody) =>
        SHA256.HashData(Encoding.UTF8.GetBytes($"{method}\n{path}\n{canonicalBody}"));
}

public sealed record StoredResponse(int StatusCode, string Body);

public abstract record ClaimResult
{
    private ClaimResult()
    {
    }

    /// <summary>The key is new. The request runs inside the scope's transaction.</summary>
    public sealed record Claimed : ClaimResult;

    /// <summary>The same request already completed; replay its response.</summary>
    public sealed record Replay(StoredResponse Response) : ClaimResult;

    /// <summary>The key was used for a different request.</summary>
    public sealed record KeyReused : ClaimResult;

    /// <summary>Another request with this key is still running (lock_timeout expired).</summary>
    public sealed record InProgress : ClaimResult;
}

/// <summary>
/// One database transaction per idempotent request (design doc §5.1). The key row is claimed at
/// the start and completed at the end of the same transaction that does the ledger work, so it is
/// never visible half-done. Stores enlist in this transaction when it is active.
/// Disposing without <see cref="CompleteAsync"/> rolls everything back.
/// </summary>
public interface IIdempotencyScope : IAsyncDisposable
{
    Task<ClaimResult> BeginAsync(IdempotencyKey key, CancellationToken cancellationToken);

    /// <summary>Stores the response against the key and commits.</summary>
    Task CompleteAsync(StoredResponse response, CancellationToken cancellationToken);
}
