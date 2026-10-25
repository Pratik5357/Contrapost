namespace Contrapost.Application.Clients;

public interface IClientDirectory
{
    /// <summary>True if the client exists and has not been revoked.</summary>
    Task<bool> IsActiveAsync(Guid clientId, CancellationToken cancellationToken);
}
