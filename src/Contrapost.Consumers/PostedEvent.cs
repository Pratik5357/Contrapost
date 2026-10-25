using System.Text.Json;
using Contrapost.Infrastructure.Messaging;

namespace Contrapost.Consumers;

/// <summary>A leg in transfer.posted, transfer.reversed or hold.captured (design doc §9.1).</summary>
public sealed record PostedLeg(Guid AccountId, string Direction, long Amount, long AccountSeq, string NormalSide, long BalanceAfter)
{
    /// <summary>True when this leg moved the account's balance down (a debit on a credit-normal account, or vice versa).</summary>
    public bool Decreases => Direction != NormalSide;
}

public sealed record PostedEvent(Guid TransactionId, IReadOnlyList<PostedLeg> Legs)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static PostedEvent From(EventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return envelope.Data.Deserialize<PostedEvent>(Json) is { Legs.Count: > 0 } posted
            ? posted
            : throw new JsonException($"Event {envelope.Id} ({envelope.Type}) has no legs.");
    }
}
