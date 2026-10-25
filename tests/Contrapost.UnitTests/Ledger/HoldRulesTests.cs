using Contrapost.Domain;
using Contrapost.Domain.Ledger;

namespace Contrapost.UnitTests.Ledger;

public class HoldRulesTests
{
    private static readonly Guid Alice = Guid.NewGuid();
    private static readonly Guid Shop = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private static LockedAccount Wallet(Guid id, long credits, long debitsPending = 0, AccountStatus status = AccountStatus.Active) =>
        new(id, "INR", NormalSide.Credit, status, OverdraftRule.DebitsMustNotExceedCredits, new Balance(0, credits, debitsPending, 0));

    private static HoldState Hold(long amount = 1000, long captured = 0, HoldStatus status = HoldStatus.Pending, int expiresInMinutes = 10) =>
        new(Guid.NewGuid(), Alice, Shop, "INR", amount, captured, status, Now.AddMinutes(expiresInMinutes));

    [Fact]
    public void Create_reserves_on_both_sides_without_posting()
    {
        var accounts = new Dictionary<Guid, LockedAccount> { [Alice] = Wallet(Alice, 5000), [Shop] = Wallet(Shop, 0) };

        var change = HoldRules.Create("INR", Alice, Shop, 1000, accounts).Value;

        Assert.Equal(new Balance(0, 5000, 1000, 0), change.Debit);
        Assert.Equal(new Balance(0, 0, 0, 1000), change.Credit);
        Assert.Equal(4000, change.Debit.Available(NormalSide.Credit));
        Assert.Equal(HoldStatus.Pending, change.Status);
    }

    [Fact]
    public void Create_counts_existing_holds_against_the_available_balance()
    {
        var accounts = new Dictionary<Guid, LockedAccount> { [Alice] = Wallet(Alice, 5000, debitsPending: 4500), [Shop] = Wallet(Shop, 0) };

        var error = HoldRules.Create("INR", Alice, Shop, 1000, accounts).Error;

        Assert.Equal("insufficient-funds", error?.Code);
        Assert.Equal("Available balance 500 is less than the debit of 1000.", error?.Detail);
    }

    [Fact]
    public void Partial_captures_settle_pending_into_posted_until_nothing_remains()
    {
        var hold = Hold(amount: 1000);
        var alice = Wallet(Alice, 5000, debitsPending: 1000);
        var shop = new LockedAccount(Shop, "INR", NormalSide.Credit, AccountStatus.Active, OverdraftRule.None, new Balance(0, 0, 0, 1000));

        var first = HoldRules.Capture(hold, 400, Now, alice, shop).Value;
        Assert.Equal(HoldStatus.PartiallyCaptured, first.Status);
        Assert.Equal(new Balance(400, 5000, 600, 0), first.Debit);
        Assert.Equal(new Balance(0, 400, 0, 600), first.Credit);

        var rest = HoldRules.Capture(hold with { CapturedAmount = 400, Status = HoldStatus.PartiallyCaptured }, null, Now,
            alice with { Balance = first.Debit }, shop with { Balance = first.Credit }).Value;
        Assert.Equal(HoldStatus.Captured, rest.Status);
        Assert.Equal(1000, rest.CapturedAmount);
        Assert.Equal(new Balance(1000, 5000, 0, 0), rest.Debit);
    }

    [Fact]
    public void Capture_more_than_remaining_is_a_business_rejection()
    {
        var error = HoldRules.Capture(Hold(amount: 1000, captured: 700, status: HoldStatus.PartiallyCaptured), 400, Now,
            Wallet(Alice, 5000, 300), Wallet(Shop, 0)).Error;

        Assert.Equal(ErrorKind.Rejected, error?.Kind);
        Assert.Equal("capture-exceeds-remaining", error?.Code);
    }

    [Fact]
    public void Capture_checks_expiry_itself_rather_than_waiting_for_the_job()
    {
        var expired = Hold(expiresInMinutes: 0); // expires_at == now counts as expired

        Assert.Equal("hold-expired", HoldRules.Capture(expired, 100, Now, Wallet(Alice, 5000, 1000), Wallet(Shop, 0)).Error?.Code);
    }

    [Theory]
    [InlineData(HoldStatus.Captured)]
    [InlineData(HoldStatus.Voided)]
    [InlineData(HoldStatus.Expired)]
    public void Closed_holds_cannot_be_captured_or_voided(HoldStatus status)
    {
        var hold = Hold(captured: status == HoldStatus.Captured ? 1000 : 0, status: status);

        Assert.Equal("hold-closed", HoldRules.Capture(hold, 1, Now, Wallet(Alice, 5000), Wallet(Shop, 0)).Error?.Code);
        Assert.Equal("hold-closed", HoldRules.Release(hold, HoldStatus.Voided, Now, Wallet(Alice, 5000), Wallet(Shop, 0)).Error?.Code);
    }

    [Fact]
    public void Void_releases_only_what_remains_after_partial_captures()
    {
        var hold = Hold(amount: 1000, captured: 400, status: HoldStatus.PartiallyCaptured);

        var change = HoldRules.Release(hold, HoldStatus.Voided, Now, Wallet(Alice, 5000, debitsPending: 600),
            new LockedAccount(Shop, "INR", NormalSide.Credit, AccountStatus.Active, OverdraftRule.None, new Balance(0, 400, 0, 600))).Value;

        Assert.Equal(HoldStatus.Voided, change.Status);
        Assert.Equal(0, change.Debit.DebitsPending);
        Assert.Equal(0, change.Credit.CreditsPending);
        Assert.Equal(400, change.CapturedAmount);
    }

    [Fact]
    public void Expiry_refuses_holds_that_are_not_due()
    {
        Assert.Throws<InvalidOperationException>(() =>
            HoldRules.Release(Hold(expiresInMinutes: 5), HoldStatus.Expired, Now, Wallet(Alice, 5000, 1000), Wallet(Shop, 0)));
    }

    [Fact]
    public void Remaining_is_zero_once_a_hold_is_final()
    {
        Assert.Equal(600, Hold(captured: 400, status: HoldStatus.PartiallyCaptured).Remaining);
        Assert.Equal(0, Hold(captured: 400, status: HoldStatus.Voided).Remaining);
    }

    [Theory]
    [InlineData(null, "currency")]
    [InlineData("INR", "amount")]
    public void Validate_reports_missing_fields(string? currency, string field)
    {
        var error = HoldRules.Validate(currency, Alice, Shop, currency is null ? 10 : 0, null);

        Assert.True(error?.Errors.ContainsKey(field));
    }

    [Fact]
    public void Validate_rejects_the_same_account_on_both_sides_and_out_of_range_expiry()
    {
        Assert.True(HoldRules.Validate("INR", Alice, Alice, 10, null)?.Errors.ContainsKey("credit_account_id"));
        Assert.True(HoldRules.Validate("INR", Alice, Shop, 10, 0)?.Errors.ContainsKey("expires_in_seconds"));
    }
}

public class ReversalRulesTests
{
    [Fact]
    public void Reversed_swaps_every_leg()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        var reversed = TransferRules.Reversed([new TransferLeg(a, Direction.Debit, 50), new TransferLeg(b, Direction.Credit, 50)]);

        Assert.Equal([new TransferLeg(a, Direction.Credit, 50), new TransferLeg(b, Direction.Debit, 50)], reversed);
    }

    [Fact]
    public void Reversals_may_touch_frozen_accounts_but_not_overdraw_them()
    {
        var bob = Guid.NewGuid();
        var alice = Guid.NewGuid();
        var accounts = new Dictionary<Guid, LockedAccount>
        {
            [bob] = new(bob, "INR", NormalSide.Credit, AccountStatus.Frozen, OverdraftRule.DebitsMustNotExceedCredits, new Balance(0, 300, 0, 0)),
            [alice] = new(alice, "INR", NormalSide.Credit, AccountStatus.Active, OverdraftRule.DebitsMustNotExceedCredits, new Balance(0, 0, 0, 0)),
        };
        var legs = new[] { new TransferLeg(bob, Direction.Debit, 300), new TransferLeg(alice, Direction.Credit, 300) };

        Assert.Equal("account-not-active", TransferRules.Apply("INR", legs, accounts).Error?.Code);
        Assert.True(TransferRules.Apply("INR", legs, accounts, isReversal: true).IsSuccess);

        var tooMuch = new[] { new TransferLeg(bob, Direction.Debit, 301), new TransferLeg(alice, Direction.Credit, 301) };
        Assert.Equal("insufficient-funds", TransferRules.Apply("INR", tooMuch, accounts, isReversal: true).Error?.Code);
    }
}
