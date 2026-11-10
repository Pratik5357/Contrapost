# ADR-007: Holds are the record of pending money; postings record only money that moved

- **Status:** Accepted
- **Date:** 2026-11-10 (decided at design time; recorded in M8)

## Context

A hold reserves money without moving it, like a card authorization: it can be captured in full or in parts, voided, or left to expire (design doc §4.4). The reservation has to reduce the available balance at once, so the same money can't be spent twice. One common design writes "pending postings" when the hold is created and converts or cancels them later.

## Decision

Postings record only money that has actually moved. A hold is its own row (`holds`), and the reserved amount lives in each account's `debits_pending` and `credits_pending` totals.

- **Create** adds the amount to both accounts' pending totals after checking the available balance. No transaction or postings.
- **Capture** creates a normal `capture` transaction with posted postings for the captured amount and moves it from pending to posted.
- **Void and expire** subtract the remaining amount from the pending totals. No transaction or postings.

## Consequences

- **Postings stay append-only and final.** No posting is ever converted, cancelled or deleted, so the history needs no status column, and the append-only guard (`CP002`) has no exceptions.
- **Two sources of truth, each checkable.** Posted totals must equal the sums of postings (I3); pending totals must equal the remaining amounts of open holds (I5); each hold's `captured_amount` must equal its capture postings (I6). Reconciliation checks all three.
- **Overdraft rules cover holds.** The `CHECK` constraint is `debits_posted + debits_pending <= credits_posted`, so a hold can never reserve money the account doesn't have.
- **A hold's lifecycle is enforced in the database too.** A trigger keeps terminal states final, terms immutable and `captured_amount` from decreasing (`CP003`).
- **Void and expire leave no postings.** The hold row and its `hold.voided` or `hold.expired` outbox message are the record, which is what an auditor would look for anyway.
