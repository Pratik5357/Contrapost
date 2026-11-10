# ADR-002: Money is an integer number of minor units

- **Status:** Accepted
- **Date:** 2026-11-10 (decided at design time; recorded in M8)

## Context

Every amount the ledger stores or returns is money. Binary floating point can't represent most decimal fractions exactly (0.1 + 0.2 ≠ 0.3), so sums drift. `decimal` types are exact but bring scale and rounding questions to every arithmetic operation and every serializer.

## Decision

Store and transmit amounts as **integers in the currency's minor unit** (paise for INR, cents for USD): `bigint` in Postgres, `long` in C#, a JSON integer on the wire. `100000` means ₹1,000.00. Formatting with a decimal point happens only at the edge, in the dashboard.

## Consequences

- **Exact arithmetic.** Every balance, every invariant check (Σ debits = Σ credits) and every reconciliation comparison is integer equality. No rounding mode to choose, nothing to drift.
- **Range.** A signed 64-bit integer holds about 9.2 × 10¹⁸ minor units, roughly ₹92 quadrillion, far beyond anything one account will see. Postgres `CHECK` constraints keep totals non-negative.
- **Clients must know the exponent.** INR and USD have 2 decimals, JPY has 0, a few currencies have 3. The API returns the currency code with every amount; the dashboard asks `Intl.NumberFormat` for the currency's fraction digits rather than assuming 2.
- **JavaScript.** JSON numbers above 2⁵³ lose precision in a browser. Amounts that large are unrealistic here, and the dashboard only displays them, but an API for real money at that scale would send amounts as strings.
- **No fractional minor units.** Interest or FX at sub-paise precision would need its own representation. Both are non-goals for v1.
