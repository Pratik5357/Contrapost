using System.Buffers.Text;
using System.Globalization;
using System.Text;

namespace Contrapost.Api.Http;

/// <summary>Opaque keyset cursors (design doc §7.4): entry pages by account_seq, admin pages by row id.</summary>
internal static class Cursors
{
    private const string SeqPrefix = "seq:";
    private const string IdPrefix = "id:";

    public static string EncodeSeq(long accountSeq) => Encode(SeqPrefix + accountSeq.ToString(CultureInfo.InvariantCulture));

    public static long? DecodeSeq(string cursor) =>
        Decode(cursor, SeqPrefix) is { } text && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var seq)
            ? seq
            : null;

    public static string EncodeId(Guid id) => Encode(IdPrefix + id.ToString("N"));

    public static Guid? DecodeId(string cursor) =>
        Decode(cursor, IdPrefix) is { } text && Guid.TryParseExact(text, "N", out var id) ? id : null;

    private static string Encode(string text) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(text));

    private static string? Decode(string cursor, string prefix)
    {
        try
        {
            var text = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(cursor));
            return text.StartsWith(prefix, StringComparison.Ordinal) ? text[prefix.Length..] : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
