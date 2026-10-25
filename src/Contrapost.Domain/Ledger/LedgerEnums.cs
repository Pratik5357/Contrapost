namespace Contrapost.Domain.Ledger;

public enum Direction
{
    Debit,
    Credit,
}

/// <summary>Whether an account's balance grows with credits or with debits (design doc §4.1).</summary>
public enum NormalSide
{
    Debit,
    Credit,
}

public enum AccountStatus
{
    Active,
    Frozen,
    Closed,
}
