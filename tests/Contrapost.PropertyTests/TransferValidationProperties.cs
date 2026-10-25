using Contrapost.Domain.Ledger;
using FsCheck.Xunit;

namespace Contrapost.PropertyTests;

/// <summary>TransferRules.Validate accepts exactly the transfers the design doc allows (§4.2, §7.4).</summary>
public class TransferValidationProperties
{
    private static readonly Guid[] Accounts = [.. Enumerable.Range(0, 12).Select(_ => Guid.NewGuid())];

    /// <summary>Every two ints decode to one leg: an account (from a pool of 12) and a signed amount (sign = direction).</summary>
    [Property(MaxTest = 1_000)]
    public bool Validate_accepts_exactly_balanced_well_formed_transfers(int[] encoded)
    {
        var legs = new List<TransferLeg>();
        for (var i = 0; i + 1 < encoded.Length; i += 2)
        {
            var amount = (long)(encoded[i + 1] % 2_000);
            legs.Add(new TransferLeg(Accounts[Math.Abs(encoded[i] % Accounts.Length)], amount < 0 ? Direction.Credit : Direction.Debit, Math.Abs(amount)));
        }

        var accepted = TransferRules.Validate("INR", legs) is null;

        var wellFormed = legs.Count is >= TransferRules.MinLegs and <= TransferRules.MaxLegs
                         && legs.All(l => l.Amount > 0)
                         && legs.Select(l => l.AccountId).Distinct().Count() == legs.Count
                         && legs.Where(l => l.Direction == Direction.Debit).Sum(l => l.Amount)
                         == legs.Where(l => l.Direction == Direction.Credit).Sum(l => l.Amount);

        return accepted == wellFormed;
    }

    [Property(MaxTest = 1_000)]
    public bool A_valid_transfer_reversed_is_still_valid_and_reversing_twice_restores_it(int seed)
    {
        var random = new Random(seed);
        var count = random.Next(TransferRules.MinLegs, TransferRules.MaxLegs + 1);
        var amounts = Enumerable.Range(0, count - 1).Select(_ => (long)random.Next(1, 1_000)).ToList();
        var legs = amounts.Select((a, i) => new TransferLeg(Accounts[i], Direction.Credit, a))
            .Append(new TransferLeg(Accounts[count - 1], Direction.Debit, amounts.Sum()))
            .ToList();

        var reversed = TransferRules.Reversed(legs);

        return TransferRules.Validate("INR", legs) is null
               && TransferRules.Validate("INR", reversed) is null
               && TransferRules.Reversed(reversed).SequenceEqual(legs);
    }
}
