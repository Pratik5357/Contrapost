-- 002: read models owned by the event consumers (design doc §9.3).
-- Written only by the consumers process, from events; never by the ledger write path.

-- Statement projector: one row per posting, keyed like postings so a redelivered event is a no-op.
CREATE TABLE account_statements (
    account_id       uuid NOT NULL,
    account_seq      bigint NOT NULL,
    transaction_id   uuid NOT NULL,
    direction        direction NOT NULL,
    amount           bigint NOT NULL CHECK (amount > 0),
    balance_after    bigint NOT NULL,          -- posted balance on the account's normal side
    posted_at        timestamptz NOT NULL,
    PRIMARY KEY (account_id, account_seq)
);

-- How far each account's statement is complete. Events can arrive out of order across relays,
-- so complete_through_seq only advances over a gapless run of account_seq.
CREATE TABLE account_statement_heads (
    account_id            uuid PRIMARY KEY,
    complete_through_seq  bigint NOT NULL DEFAULT 0,
    updated_at            timestamptz NOT NULL DEFAULT now()
);

-- Low-balance notifier: a side effect that must happen once per event, deduplicated by the inbox.
CREATE TABLE low_balance_alerts (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    account_id      uuid NOT NULL,
    transaction_id  uuid NOT NULL,
    balance_after   bigint NOT NULL,
    threshold       bigint NOT NULL,
    created_at      timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_low_balance_alerts_account ON low_balance_alerts (account_id, created_at DESC);
