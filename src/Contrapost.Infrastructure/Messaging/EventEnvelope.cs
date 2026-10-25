using System.Text.Json;

namespace Contrapost.Infrastructure.Messaging;

/// <summary>
/// CloudEvents-style envelope (design doc §9.1). <see cref="Id"/> is the outbox message id and is
/// what consumers deduplicate on; <see cref="Subject"/> is the aggregate (transaction, hold or account).
/// </summary>
public sealed record EventEnvelope(Guid Id, string Type, string Source, Guid Subject, DateTimeOffset Time, JsonElement Data)
{
    public const string ApiSource = "contrapost/api";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(this, Json);

    /// <summary>Parses a delivered body; throws <see cref="JsonException"/> for anything that isn't an envelope.</summary>
    public static EventEnvelope Parse(ReadOnlySpan<byte> body) =>
        JsonSerializer.Deserialize<EventEnvelope>(body, Json) is { Id: var id, Type: { Length: > 0 } } envelope && id != Guid.Empty
            ? envelope
            : throw new JsonException("Message is not an event envelope.");
}
