using Contrapost.Domain.Ledger;

namespace Contrapost.UnitTests.Ledger;

public class BalanceTests
{
    [Fact]
    public void Credit_normal_balance_grows_with_credits_and_available_subtracts_pending_debits()
    {
        var balance = new Balance(DebitsPosted: 300, CreditsPosted: 1000, DebitsPending: 200, CreditsPending: 50);

        Assert.Equal(700, balance.Posted(NormalSide.Credit));
        Assert.Equal(500, balance.Available(NormalSide.Credit));
    }

    [Fact]
    public void Debit_normal_balance_grows_with_debits_and_available_subtracts_pending_credits()
    {
        var balance = new Balance(DebitsPosted: 1000, CreditsPosted: 300, DebitsPending: 50, CreditsPending: 200);

        Assert.Equal(700, balance.Posted(NormalSide.Debit));
        Assert.Equal(500, balance.Available(NormalSide.Debit));
    }

    [Fact]
    public void Post_adds_to_the_matching_total_only()
    {
        var balance = new Balance(0, 0, 0, 0).Post(Direction.Debit, 40).Post(Direction.Credit, 100);

        Assert.Equal(new Balance(40, 100, 0, 0), balance);
    }

    [Fact]
    public void Post_throws_instead_of_wrapping_on_overflow()
    {
        var balance = new Balance(long.MaxValue, 0, 0, 0);

        Assert.Throws<OverflowException>(() => balance.Post(Direction.Debit, 1));
    }

    [Theory]
    [InlineData(1000, 0, true)]     // spend everything
    [InlineData(1001, 0, false)]    // one over
    [InlineData(800, 200, true)]    // posted + pending exactly equal to credits
    [InlineData(801, 200, false)]   // pending debits count against the balance
    public void Debits_must_not_exceed_credits(long debitsPosted, long debitsPending, bool allowed)
    {
        var balance = new Balance(debitsPosted, CreditsPosted: 1000, debitsPending, CreditsPending: 0);

        Assert.Equal(allowed, OverdraftRule.DebitsMustNotExceedCredits.Allows(balance));
    }

    [Fact]
    public void Credits_must_not_exceed_debits_is_the_mirror_rule()
    {
        Assert.True(OverdraftRule.CreditsMustNotExceedDebits.Allows(new Balance(1000, 800, 0, 200)));
        Assert.False(OverdraftRule.CreditsMustNotExceedDebits.Allows(new Balance(1000, 801, 0, 200)));
    }

    [Fact]
    public void No_rule_allows_a_negative_balance()
    {
        Assert.True(OverdraftRule.None.Allows(new Balance(DebitsPosted: 5000, 0, 0, 0)));
    }

    [Theory]
    [InlineData(NormalSide.Credit, OverdraftRule.DebitsMustNotExceedCredits)]
    [InlineData(NormalSide.Debit, OverdraftRule.CreditsMustNotExceedDebits)]
    public void Preventing_overdraft_picks_the_rule_for_the_normal_side(NormalSide side, OverdraftRule expected)
    {
        Assert.Equal(expected, OverdraftRules.PreventingOverdraft(side));
    }
}
