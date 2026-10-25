using Contrapost.Domain;
using Contrapost.Domain.Ledger;

namespace Contrapost.UnitTests.Ledger;

public class TransferRulesTests
{
    private static readonly Guid Alice = Guid.NewGuid();
    private static readonly Guid Bob = Guid.NewGuid();
    private static readonly Guid Fees = Guid.NewGuid();

    private static TransferLeg Debit(Guid account, long amount) => new(account, Direction.Debit, amount);

    private static TransferLeg Credit(Guid account, long amount) => new(account, Direction.Credit, amount);

    private static LockedAccount Wallet(Guid id, long credits, string currency = "INR", AccountStatus status = AccountStatus.Active) =>
        new(id, currency, NormalSide.Credit, status, OverdraftRule.DebitsMustNotExceedCredits, new Balance(0, credits, 0, 0));

    [Fact]
    public void Balanced_multi_leg_transfer_is_valid()
    {
        Assert.Null(TransferRules.Validate("INR", [Debit(Alice, 50000), Credit(Bob, 48500), Credit(Fees, 1500)]));
    }

    [Theory]
    [InlineData("inr")]
    [InlineData("INRR")]
    [InlineData(null)]
    public void Currency_must_be_three_uppercase_letters(string? currency)
    {
        var error = TransferRules.Validate(currency, [Debit(Alice, 1), Credit(Bob, 1)]);

        Assert.True(error?.Errors.ContainsKey("currency"));
    }

    [Fact]
    public void Fewer_than_two_or_more_than_ten_legs_is_invalid()
    {
        Assert.NotNull(TransferRules.Validate("INR", [Debit(Alice, 1)]));
        Assert.NotNull(TransferRules.Validate("INR", null));

        var eleven = Enumerable.Range(0, 11).Select(i => i == 0 ? Debit(Guid.NewGuid(), 10) : Credit(Guid.NewGuid(), 1)).ToList();
        Assert.NotNull(TransferRules.Validate("INR", eleven));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Amounts_must_be_positive(long amount)
    {
        var error = TransferRules.Validate("INR", [Debit(Alice, amount), Credit(Bob, amount)]);

        Assert.True(error?.Errors.ContainsKey("legs[0].amount"));
    }

    [Fact]
    public void Debits_must_equal_credits()
    {
        var error = TransferRules.Validate("INR", [Debit(Alice, 100), Credit(Bob, 90)]);

        Assert.NotNull(error);
        Assert.Equal(["Debits (100) must equal credits (90)."], error.Errors["legs"]);
    }

    [Fact]
    public void An_account_can_appear_in_only_one_leg()
    {
        Assert.NotNull(TransferRules.Validate("INR", [Debit(Alice, 100), Credit(Alice, 100)]));
    }

    [Fact]
    public void Sums_that_overflow_are_invalid_rather_than_wrapping()
    {
        var error = TransferRules.Validate("INR", [Debit(Alice, long.MaxValue), Debit(Bob, 1), Credit(Fees, 1)]);

        Assert.NotNull(error);
        Assert.Equal(["Amounts are too large."], error.Errors["legs"]);
    }

    [Fact]
    public void Apply_returns_the_new_balance_of_every_leg()
    {
        var accounts = new Dictionary<Guid, LockedAccount> { [Alice] = Wallet(Alice, 1000), [Bob] = Wallet(Bob, 0) };

        var result = TransferRules.Apply("INR", [Debit(Alice, 400), Credit(Bob, 400)], accounts);

        Assert.True(result.IsSuccess);
        Assert.Equal(600, result.Value[Alice].Posted(NormalSide.Credit));
        Assert.Equal(400, result.Value[Bob].Posted(NormalSide.Credit));
    }

    [Fact]
    public void Apply_rejects_spending_more_than_the_available_balance()
    {
        var alice = Wallet(Alice, 1000) with { Balance = new Balance(0, 1000, DebitsPending: 700, 0) };
        var accounts = new Dictionary<Guid, LockedAccount> { [Alice] = alice, [Bob] = Wallet(Bob, 0) };

        var error = TransferRules.Apply("INR", [Debit(Alice, 400), Credit(Bob, 400)], accounts).Error;

        Assert.Equal("insufficient-funds", error?.Code);
        Assert.Equal(ErrorKind.Rejected, error?.Kind);
        Assert.Equal("Available balance 300 is less than the debit of 400.", error?.Detail);
    }

    [Fact]
    public void Apply_reports_accounts_it_was_not_given_as_not_found()
    {
        var accounts = new Dictionary<Guid, LockedAccount> { [Alice] = Wallet(Alice, 1000) };

        var error = TransferRules.Apply("INR", [Debit(Alice, 1), Credit(Bob, 1)], accounts).Error;

        Assert.Equal(ErrorKind.NotFound, error?.Kind);
    }

    [Fact]
    public void Apply_rejects_a_currency_mismatch()
    {
        var accounts = new Dictionary<Guid, LockedAccount> { [Alice] = Wallet(Alice, 1000), [Bob] = Wallet(Bob, 0, currency: "USD") };

        Assert.Equal("currency-mismatch", TransferRules.Apply("INR", [Debit(Alice, 1), Credit(Bob, 1)], accounts).Error?.Code);
    }

    [Theory]
    [InlineData(AccountStatus.Frozen)]
    [InlineData(AccountStatus.Closed)]
    public void Apply_rejects_accounts_that_are_not_active(AccountStatus status)
    {
        var accounts = new Dictionary<Guid, LockedAccount> { [Alice] = Wallet(Alice, 1000), [Bob] = Wallet(Bob, 0, status: status) };

        Assert.Equal("account-not-active", TransferRules.Apply("INR", [Debit(Alice, 1), Credit(Bob, 1)], accounts).Error?.Code);
    }
}
