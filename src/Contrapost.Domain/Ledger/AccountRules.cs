using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Contrapost.Domain.Ledger;

public static partial class AccountRules
{
    public const int MaxCodeLength = 100;

    public static bool IsValidCurrency([NotNullWhen(true)] string? currency) =>
        currency is not null && CurrencyPattern().IsMatch(currency);

    public static LedgerError? ValidateNew(string? code, string? currency, NormalSide? normalSide)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(code) || code.Length > MaxCodeLength || !CodePattern().IsMatch(code))
        {
            errors["code"] = [$"Required; up to {MaxCodeLength} characters of letters, digits and . _ : -"];
        }

        if (!IsValidCurrency(currency))
        {
            errors["currency"] = ["Must be a three-letter uppercase ISO 4217 code."];
        }

        if (normalSide is null)
        {
            errors["normal_side"] = ["Required: debit or credit."];
        }

        return errors.Count == 0 ? null : LedgerError.Validation(errors);
    }

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex CurrencyPattern();

    // e.g. "wallet:alice", "settlement:INR:0"
    [GeneratedRegex("^[A-Za-z0-9._:-]+$")]
    private static partial Regex CodePattern();
}
