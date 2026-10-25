-- Contrapost schema (design doc §6, revision 2). This is migration 001, the baseline.
-- Do not edit it: later changes go in db/migrations/NNN_name.sql and are applied in order.
-- EF Core migrations embed these files rather than generating DDL, so the guards below
-- stay exactly as written.
--
-- Custom SQLSTATEs raised by the guards (the API maps these to Problem Details):
--   CP001  unbalanced transaction (Σ debits ≠ Σ credits, or fewer than 2 postings)
--   CP002  append-only table modified
--   CP003  illegal hold change
--   CP004  illegal reversal target

-- ===== Clients =====
CREATE TABLE api_clients (
    id               uuid PRIMARY KEY,
    name             text NOT NULL,
    key_prefix       text NOT NULL UNIQUE,      -- e.g. "cp_live_ab12", shown in UI
    key_hash         bytea NOT NULL,            -- SHA-256 of the secret part
    scopes           text[] NOT NULL,           -- accounts:write, transfers:write, ...
    rate_limit_rpm   int NOT NULL DEFAULT 600,
    created_at       timestamptz NOT NULL DEFAULT now(),
    revoked_at       timestamptz
);

-- ===== Accounts =====
CREATE TYPE normal_side AS ENUM ('debit', 'credit');
CREATE TYPE account_status AS ENUM ('active', 'frozen', 'closed');

CREATE TABLE accounts (
    id                              uuid PRIMARY KEY,
    client_id                       uuid NOT NULL REFERENCES api_clients(id),
    code                            text NOT NULL,          -- "wallet:alice"
    currency                        char(3) NOT NULL,
    normal_side                     normal_side NOT NULL,
    debits_must_not_exceed_credits  boolean NOT NULL DEFAULT false,
    credits_must_not_exceed_debits  boolean NOT NULL DEFAULT false,
    status                          account_status NOT NULL DEFAULT 'active',
    metadata                        jsonb NOT NULL DEFAULT '{}',
    created_at                      timestamptz NOT NULL DEFAULT now(),
    UNIQUE (client_id, code),
    -- targets for composite foreign keys (tenant + currency, overdraft flags)
    UNIQUE (id, client_id, currency),
    UNIQUE (id, debits_must_not_exceed_credits, credits_must_not_exceed_debits),
    CHECK (NOT (debits_must_not_exceed_credits AND credits_must_not_exceed_debits))
);

CREATE TABLE account_balances (
    account_id                      uuid PRIMARY KEY,
    -- copied from accounts so the overdraft rule can be a CHECK; the composite
    -- FK keeps the copies in sync and makes the flags immutable once set
    debits_must_not_exceed_credits  boolean NOT NULL,
    credits_must_not_exceed_debits  boolean NOT NULL,
    debits_posted    bigint NOT NULL DEFAULT 0 CHECK (debits_posted   >= 0),
    credits_posted   bigint NOT NULL DEFAULT 0 CHECK (credits_posted  >= 0),
    debits_pending   bigint NOT NULL DEFAULT 0 CHECK (debits_pending  >= 0),
    credits_pending  bigint NOT NULL DEFAULT 0 CHECK (credits_pending >= 0),
    entry_seq        bigint NOT NULL DEFAULT 0,  -- number of postings; gapless per account
    version          bigint NOT NULL DEFAULT 0,  -- bumped on every change (optimistic strategy B)
    updated_at       timestamptz NOT NULL DEFAULT now(),
    FOREIGN KEY (account_id, debits_must_not_exceed_credits, credits_must_not_exceed_debits)
        REFERENCES accounts (id, debits_must_not_exceed_credits, credits_must_not_exceed_debits),
    CONSTRAINT ck_overdraft_debits
        CHECK (NOT debits_must_not_exceed_credits OR debits_posted + debits_pending <= credits_posted),
    CONSTRAINT ck_overdraft_credits
        CHECK (NOT credits_must_not_exceed_debits OR credits_posted + credits_pending <= debits_posted)
);

-- ===== Holds =====
-- Holds are the record of pending money. They never write postings; only a
-- capture does, through a 'capture' transaction.
CREATE TYPE hold_status AS ENUM ('pending', 'partially_captured', 'captured', 'voided', 'expired');

CREATE TABLE holds (
    id                 uuid PRIMARY KEY,
    client_id          uuid NOT NULL REFERENCES api_clients(id),
    debit_account_id   uuid NOT NULL,
    credit_account_id  uuid NOT NULL,
    currency           char(3) NOT NULL,
    amount             bigint NOT NULL CHECK (amount > 0),
    captured_amount    bigint NOT NULL DEFAULT 0,
    status             hold_status NOT NULL DEFAULT 'pending',
    expires_at         timestamptz NOT NULL,
    created_at         timestamptz NOT NULL DEFAULT now(),
    updated_at         timestamptz NOT NULL DEFAULT now(),
    UNIQUE (id, client_id, currency),
    FOREIGN KEY (debit_account_id, client_id, currency)  REFERENCES accounts (id, client_id, currency),
    FOREIGN KEY (credit_account_id, client_id, currency) REFERENCES accounts (id, client_id, currency),
    CHECK (debit_account_id <> credit_account_id),
    CHECK (captured_amount BETWEEN 0 AND amount),
    CHECK (status <> 'pending'            OR captured_amount = 0),
    CHECK (status <> 'partially_captured' OR captured_amount BETWEEN 1 AND amount - 1),
    CHECK (status <> 'captured'           OR captured_amount = amount)
);
CREATE INDEX ix_holds_expiry ON holds (expires_at) WHERE status IN ('pending', 'partially_captured');

-- ===== Transactions & postings (append-only) =====
CREATE TYPE txn_kind AS ENUM ('transfer', 'capture', 'reversal');
CREATE TYPE direction AS ENUM ('debit', 'credit');

CREATE TABLE transactions (
    id                       uuid PRIMARY KEY,
    client_id                uuid NOT NULL REFERENCES api_clients(id),
    kind                     txn_kind NOT NULL,
    currency                 char(3) NOT NULL,
    reference                text,                    -- client's own reference
    description              text,
    hold_id                  uuid,
    reverses_transaction_id  uuid,
    metadata                 jsonb NOT NULL DEFAULT '{}',
    created_at               timestamptz NOT NULL DEFAULT now(),
    UNIQUE (id, client_id, currency),
    FOREIGN KEY (hold_id, client_id, currency) REFERENCES holds (id, client_id, currency),
    FOREIGN KEY (reverses_transaction_id, client_id, currency) REFERENCES transactions (id, client_id, currency),
    CHECK ((kind = 'capture')  = (hold_id IS NOT NULL)),
    CHECK ((kind = 'reversal') = (reverses_transaction_id IS NOT NULL))
);
CREATE UNIQUE INDEX ux_txn_single_reversal
    ON transactions (reverses_transaction_id) WHERE reverses_transaction_id IS NOT NULL;
-- long-term dedupe beyond the 24 h idempotency-key retention
CREATE UNIQUE INDEX ux_txn_transfer_reference
    ON transactions (client_id, reference) WHERE kind = 'transfer' AND reference IS NOT NULL;
CREATE INDEX ix_txn_hold ON transactions (hold_id) WHERE hold_id IS NOT NULL;

CREATE TABLE postings (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    transaction_id  uuid NOT NULL,
    client_id       uuid NOT NULL,
    currency        char(3) NOT NULL,
    account_id      uuid NOT NULL,
    account_seq     bigint NOT NULL CHECK (account_seq > 0),  -- account_balances.entry_seq after this posting
    direction       direction NOT NULL,
    amount          bigint NOT NULL CHECK (amount > 0),
    created_at      timestamptz NOT NULL DEFAULT now(),
    -- same tenant and same currency as both the transaction and the account
    FOREIGN KEY (transaction_id, client_id, currency) REFERENCES transactions (id, client_id, currency),
    FOREIGN KEY (account_id, client_id, currency)     REFERENCES accounts (id, client_id, currency),
    UNIQUE (account_id, account_seq),      -- also the keyset index for /accounts/{id}/entries
    UNIQUE (transaction_id, account_id)    -- an account appears at most once per transaction
);

-- ===== Idempotency =====
-- The row is inserted at the start of the request and completed in the same
-- DB transaction, so other sessions never see it half-done (design doc §5.1).
CREATE TABLE idempotency_keys (
    client_id       uuid NOT NULL REFERENCES api_clients(id),
    key             text NOT NULL,
    request_method  text NOT NULL,
    request_path    text NOT NULL,
    request_hash    bytea NOT NULL,
    response_code   int,
    response_body   jsonb,
    created_at      timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (client_id, key),
    CHECK (response_body IS NULL OR response_code IS NOT NULL)
);
CREATE INDEX ix_idempotency_created ON idempotency_keys (created_at);

-- ===== Outbox / inbox =====
CREATE TABLE outbox_messages (
    id               uuid PRIMARY KEY,
    aggregate_id     uuid NOT NULL,          -- transaction, hold or account id
    type             text NOT NULL,          -- "transfer.posted"
    payload          jsonb NOT NULL,
    created_at       timestamptz NOT NULL DEFAULT now(),
    next_attempt_at  timestamptz NOT NULL DEFAULT now(),
    published_at     timestamptz,
    attempts         int NOT NULL DEFAULT 0,
    last_error       text
);
CREATE INDEX ix_outbox_unpublished ON outbox_messages (created_at) WHERE published_at IS NULL;

CREATE TABLE inbox_messages (                -- consumer-side dedupe
    consumer      text NOT NULL,
    message_id    uuid NOT NULL,
    processed_at  timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (consumer, message_id)
);

-- ===== Reconciliation =====
CREATE TABLE reconciliation_runs (
    id               uuid PRIMARY KEY,
    started_at       timestamptz NOT NULL,
    finished_at      timestamptz,
    accounts_checked int,
    findings_count   int,
    status           text NOT NULL CHECK (status IN ('running', 'clean', 'drift_found', 'failed'))
);

CREATE TABLE reconciliation_findings (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    run_id      uuid NOT NULL REFERENCES reconciliation_runs(id),
    check_name  text NOT NULL,             -- balance_drift | unbalanced_txn | ...
    subject_id  uuid,
    expected    jsonb,
    actual      jsonb
);

-- =====================================================================
-- Guards
-- =====================================================================

-- ----- Append-only: postings and transactions -----
CREATE FUNCTION forbid_modification() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION '% on % is not allowed: the ledger is append-only', TG_OP, TG_TABLE_NAME
        USING ERRCODE = 'CP002';
END $$;

CREATE TRIGGER postings_append_only
    BEFORE UPDATE OR DELETE OR TRUNCATE ON postings
    FOR EACH STATEMENT EXECUTE FUNCTION forbid_modification();
CREATE TRIGGER transactions_append_only
    BEFORE UPDATE OR DELETE OR TRUNCATE ON transactions
    FOR EACH STATEMENT EXECUTE FUNCTION forbid_modification();

-- ----- Balanced transactions, checked at commit -----
-- Fired from both tables: the transactions trigger catches a transaction with
-- no postings at all, the postings trigger catches postings added later.
-- TG_ARGV[0] names the column holding the transaction id.
CREATE FUNCTION assert_transaction_balanced() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    txn_id   uuid := (to_jsonb(NEW) ->> TG_ARGV[0])::uuid;
    n        bigint;
    debits   numeric;
    credits  numeric;
BEGIN
    SELECT count(*),
           coalesce(sum(amount) FILTER (WHERE direction = 'debit'), 0),
           coalesce(sum(amount) FILTER (WHERE direction = 'credit'), 0)
      INTO n, debits, credits
      FROM postings
     WHERE transaction_id = txn_id;

    IF n < 2 OR debits <> credits THEN
        RAISE EXCEPTION 'transaction % is unbalanced: % postings, debits %, credits %',
            txn_id, n, debits, credits
            USING ERRCODE = 'CP001';
    END IF;
    RETURN NULL;
END $$;

CREATE CONSTRAINT TRIGGER transactions_balanced
    AFTER INSERT ON transactions
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION assert_transaction_balanced('id');
CREATE CONSTRAINT TRIGGER postings_balanced
    AFTER INSERT ON postings
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION assert_transaction_balanced('transaction_id');

-- ----- Reversal targets -----
CREATE FUNCTION assert_reversal_target() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    target_kind txn_kind;
BEGIN
    SELECT kind INTO target_kind FROM transactions WHERE id = NEW.reverses_transaction_id;
    -- a missing target is left to the foreign key to report
    IF target_kind IS NOT NULL AND target_kind NOT IN ('transfer', 'capture') THEN
        RAISE EXCEPTION 'transaction % is a %; only transfers and captures can be reversed',
            NEW.reverses_transaction_id, target_kind
            USING ERRCODE = 'CP004';
    END IF;
    RETURN NEW;
END $$;

CREATE TRIGGER transactions_reversal_target
    BEFORE INSERT ON transactions
    FOR EACH ROW WHEN (NEW.kind = 'reversal')
    EXECUTE FUNCTION assert_reversal_target();

-- ----- Hold lifecycle -----
CREATE FUNCTION assert_hold_change() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'holds cannot be deleted' USING ERRCODE = 'CP003';
    END IF;
    IF OLD.status IN ('captured', 'voided', 'expired') THEN
        RAISE EXCEPTION 'hold % is %, which is final', OLD.id, OLD.status USING ERRCODE = 'CP003';
    END IF;
    IF (NEW.client_id, NEW.debit_account_id, NEW.credit_account_id, NEW.currency, NEW.amount, NEW.expires_at, NEW.created_at)
       IS DISTINCT FROM
       (OLD.client_id, OLD.debit_account_id, OLD.credit_account_id, OLD.currency, OLD.amount, OLD.expires_at, OLD.created_at) THEN
        RAISE EXCEPTION 'hold % terms are immutable', OLD.id USING ERRCODE = 'CP003';
    END IF;
    IF NEW.captured_amount < OLD.captured_amount THEN
        RAISE EXCEPTION 'hold % captured_amount cannot decrease', OLD.id USING ERRCODE = 'CP003';
    END IF;
    IF NEW.status = 'pending' AND OLD.status <> 'pending' THEN
        RAISE EXCEPTION 'hold % cannot return to pending', OLD.id USING ERRCODE = 'CP003';
    END IF;
    NEW.updated_at := now();
    RETURN NEW;
END $$;

CREATE TRIGGER holds_lifecycle
    BEFORE UPDATE OR DELETE ON holds
    FOR EACH ROW EXECUTE FUNCTION assert_hold_change();
