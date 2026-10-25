using System.Globalization;
using Contrapost.Domain.Ledger;
using FsCheck.Xunit;

namespace Contrapost.PropertyTests;

/// <summary>
/// Random operation sequences, run through the real domain rules, never break the ledger's invariants
/// (design doc §8.3, §13). FsCheck generates the script as an int array, which it can shrink to a
/// minimal failing sequence; every four ints decode to one operation.
/// </summary>
public class LedgerModelProperties
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    [Property(MaxTest = 500)]
    public void Random_operations_preserve_every_invariant(int[] script)
    {
        var ledger = new Ledger();

        for (var i = 0; i + 3 < script.Length; i += 4)
        {
            var (kind, a, b, amount) = (Math.Abs(script[i] % 6), Math.Abs(script[i + 1]), Math.Abs(script[i + 2]), Math.Abs(script[i + 3] % 5_000) + 1);
            var description = kind switch
            {
                0 => ledger.Transfer(a, b, amount),
                1 => ledger.CreateHold(a, b, amount),
                2 => ledger.Capture(a, amount % 3 == 0 ? null : amount),
                3 => ledger.Void(a),
                4 => ledger.Expire(a),
                _ => ledger.Reverse(a),
            };

            ledger.AssertInvariants($"after step {i / 4}: {description}");
        }
    }

    /// <summary>A small in-memory ledger whose every change goes through TransferRules and HoldRules.</summary>
    private sealed class Ledger
    {
        private readonly Dictionary<int, LockedAccount> _accounts = [];
        private readonly List<HoldState> _holds = [];
        private readonly List<(IReadOnlyList<TransferLeg> Legs, bool Reversible, bool Reversed)> _transactions = [];
        private readonly List<TransferLeg> _postings = [];
        private readonly Dictionary<Guid, int> _index = [];

        public Ledger()
        {
            // 0: external funding (may go negative); 1-4: wallets; 5: a debit-normal account with the mirror rule.
            Add(0, NormalSide.Debit, OverdraftRule.None);
            for (var i = 1; i <= 4; i++)
            {
                Add(i, NormalSide.Credit, OverdraftRule.DebitsMustNotExceedCredits);
            }

            Add(5, NormalSide.Debit, OverdraftRule.CreditsMustNotExceedDebits);
            for (var i = 1; i <= 4; i++)
            {
                Transfer(0, i, 10_000);
            }
        }

        private int AccountCount => _accounts.Count;

        public string Transfer(int from, int to, long amount)
        {
            (from, to) = (from % AccountCount, to % AccountCount);
            if (from == to)
            {
                return "transfer to self (skipped)";
            }

            var legs = new[] { new TransferLeg(Id(from), Direction.Debit, amount), new TransferLeg(Id(to), Direction.Credit, amount) };
            if (TransferRules.Validate("INR", legs) is { } invalid)
            {
                throw new InvalidOperationException($"generated an invalid transfer: {invalid.Detail}");
            }

            return Post($"transfer {from}->{to} {amount}", legs, isReversal: false, reversible: true);
        }

        public string CreateHold(int from, int to, long amount)
        {
            (from, to) = (from % AccountCount, to % AccountCount);
            if (from == to)
            {
                return "hold on self (skipped)";
            }

            var change = HoldRules.Create("INR", Id(from), Id(to), amount, _accounts.Values.ToDictionary(a => a.Id));
            if (change.Error is { } error)
            {
                return $"hold {from}->{to} {amount} rejected: {error.Code}";
            }

            Set(from, change.Value.Debit);
            Set(to, change.Value.Credit);
            _holds.Add(new HoldState(Guid.NewGuid(), Id(from), Id(to), "INR", amount, 0, HoldStatus.Pending, Now.AddMinutes(10)));
            return $"hold {from}->{to} {amount}";
        }

        public string Capture(int pick, long? amount)
        {
            if (_holds.Count == 0)
            {
                return "capture with no holds (skipped)";
            }

            var index = pick % _holds.Count;
            var hold = _holds[index];
            var change = HoldRules.Capture(hold, amount, Now, Account(hold.DebitAccountId), Account(hold.CreditAccountId));
            if (change.Error is { } error)
            {
                return $"capture hold {index} {amount?.ToString(CultureInfo.InvariantCulture) ?? "rest"} rejected: {error.Code}";
            }

            var captured = change.Value.CapturedAmount - hold.CapturedAmount;
            Set(_index[hold.DebitAccountId], change.Value.Debit);
            Set(_index[hold.CreditAccountId], change.Value.Credit);
            _holds[index] = hold with { CapturedAmount = change.Value.CapturedAmount, Status = change.Value.Status };

            var legs = new[] { new TransferLeg(hold.DebitAccountId, Direction.Debit, captured), new TransferLeg(hold.CreditAccountId, Direction.Credit, captured) };
            _postings.AddRange(legs);
            _transactions.Add((legs, Reversible: true, Reversed: false));
            return $"capture hold {index} {captured}";
        }

        public string Void(int pick) => Release(pick, HoldStatus.Voided, Now);

        public string Expire(int pick) =>
            _holds.Count == 0 ? "expire with no holds (skipped)" : Release(pick, HoldStatus.Expired, _holds[pick % _holds.Count].ExpiresAt);

        public string Reverse(int pick)
        {
            if (_transactions.Count == 0)
            {
                return "reverse with no transactions (skipped)";
            }

            var index = pick % _transactions.Count;
            var (legs, reversible, reversed) = _transactions[index];
            if (!reversible || reversed)
            {
                return $"reverse transaction {index} refused (reversal or already reversed)";
            }

            var outcome = Post($"reverse transaction {index}", TransferRules.Reversed(legs), isReversal: true, reversible: false);
            if (!outcome.Contains("rejected", StringComparison.Ordinal))
            {
                _transactions[index] = (legs, reversible, Reversed: true);
            }

            return outcome;
        }

        public void AssertInvariants(string context)
        {
            var balances = _accounts.Values.Select(a => a.Balance).ToList();

            Require(balances.Sum(b => b.DebitsPosted) == balances.Sum(b => b.CreditsPosted), context, "posted debits equal posted credits (I2)");
            Require(balances.Sum(b => b.DebitsPending) == balances.Sum(b => b.CreditsPending), context, "pending debits equal pending credits (I2)");

            foreach (var account in _accounts.Values)
            {
                Require(account.OverdraftRule.Allows(account.Balance), context, $"account {_index[account.Id]} obeys its overdraft rule (I4)");

                var open = _holds.Where(h => h.IsOpen).ToList();
                Require(account.Balance.DebitsPending == open.Where(h => h.DebitAccountId == account.Id).Sum(h => h.Remaining), context,
                    $"account {_index[account.Id]} debits_pending equals its open holds (I5)");
                Require(account.Balance.CreditsPending == open.Where(h => h.CreditAccountId == account.Id).Sum(h => h.Remaining), context,
                    $"account {_index[account.Id]} credits_pending equals its open holds (I5)");

                var posted = _postings.Where(p => p.AccountId == account.Id).ToList();
                Require(account.Balance.DebitsPosted == posted.Where(p => p.Direction == Direction.Debit).Sum(p => p.Amount)
                        && account.Balance.CreditsPosted == posted.Where(p => p.Direction == Direction.Credit).Sum(p => p.Amount),
                    context, $"account {_index[account.Id]} posted totals equal its postings (I3)");
            }

            foreach (var hold in _holds)
            {
                var consistent = hold.Status switch
                {
                    HoldStatus.Pending => hold.CapturedAmount == 0,
                    HoldStatus.PartiallyCaptured => hold.CapturedAmount > 0 && hold.CapturedAmount < hold.Amount,
                    HoldStatus.Captured => hold.CapturedAmount == hold.Amount,
                    _ => hold.CapturedAmount >= 0 && hold.CapturedAmount <= hold.Amount,
                };
                Require(consistent, context, $"hold status {hold.Status} matches captured {hold.CapturedAmount} of {hold.Amount}");
            }
        }

        private string Post(string description, IReadOnlyList<TransferLeg> legs, bool isReversal, bool reversible)
        {
            var applied = TransferRules.Apply("INR", legs, _accounts.Values.ToDictionary(a => a.Id), isReversal);
            if (applied.Error is { } error)
            {
                return $"{description} rejected: {error.Code}";
            }

            foreach (var (id, balance) in applied.Value)
            {
                Set(_index[id], balance);
            }

            _postings.AddRange(legs);
            _transactions.Add((legs, reversible, Reversed: false));
            return description;
        }

        private string Release(int pick, HoldStatus outcome, DateTimeOffset now)
        {
            if (_holds.Count == 0)
            {
                return $"{outcome} with no holds (skipped)";
            }

            var index = pick % _holds.Count;
            var hold = _holds[index];
            if (outcome == HoldStatus.Expired && !hold.IsOpen)
            {
                return $"expire hold {index} skipped: already {hold.Status}";
            }

            var change = HoldRules.Release(hold, outcome, now, Account(hold.DebitAccountId), Account(hold.CreditAccountId));
            if (change.Error is { } error)
            {
                return $"{outcome} hold {index} rejected: {error.Code}";
            }

            Set(_index[hold.DebitAccountId], change.Value.Debit);
            Set(_index[hold.CreditAccountId], change.Value.Credit);
            _holds[index] = hold with { Status = change.Value.Status };
            return $"{outcome} hold {index}";
        }

        private void Add(int number, NormalSide side, OverdraftRule rule)
        {
            var id = Guid.NewGuid();
            _accounts[number] = new LockedAccount(id, "INR", side, AccountStatus.Active, rule, new Balance(0, 0, 0, 0));
            _index[id] = number;
        }

        private Guid Id(int number) => _accounts[number].Id;

        private LockedAccount Account(Guid id) => _accounts[_index[id]];

        private void Set(int number, Balance balance) => _accounts[number] = _accounts[number] with { Balance = balance };

        private static void Require(bool condition, string context, string invariant)
        {
            if (!condition)
            {
                throw new Xunit.Sdk.XunitException($"Invariant broken {context}: {invariant}");
            }
        }
    }
}
