-- Database guard tests for db/schema.sql. Run with scripts/db-test.sh.
--
-- Each check runs in its own autocommit transaction. expect_error forces
-- deferred constraint triggers to fire (SET CONSTRAINTS ALL IMMEDIATE) inside a
-- subtransaction, so the failing statement is rolled back and later checks see
-- only the fixture data.

\set ON_ERROR_STOP on
\set QUIET on
SET client_min_messages = notice;
\o /dev/null

CREATE FUNCTION pg_temp.expect_error(label text, expected_state text, stmt text) RETURNS void
LANGUAGE plpgsql AS $$
BEGIN
    BEGIN
        EXECUTE stmt;
        SET CONSTRAINTS ALL IMMEDIATE;
    EXCEPTION WHEN OTHERS THEN
        IF SQLSTATE = expected_state THEN
            RAISE NOTICE 'pass  %', label;
            RETURN;
        END IF;
        RAISE EXCEPTION 'FAIL  %: expected %, got % (%)', label, expected_state, SQLSTATE, SQLERRM;
    END;
    RAISE EXCEPTION 'FAIL  %: expected %, but the statement succeeded', label, expected_state;
END $$;

CREATE FUNCTION pg_temp.expect_ok(label text, stmt text) RETURNS void
LANGUAGE plpgsql AS $$
BEGIN
    EXECUTE stmt;
    SET CONSTRAINTS ALL IMMEDIATE;
    RAISE NOTICE 'pass  %', label;
EXCEPTION WHEN OTHERS THEN
    RAISE EXCEPTION 'FAIL  %: % (%)', label, SQLERRM, SQLSTATE;
END $$;

-- ===== Fixtures =====
-- c1 owns funding (system, may go negative), alice and bob (wallets), alice_usd.
-- c2 owns eve.
INSERT INTO api_clients (id, name, key_prefix, key_hash, scopes) VALUES
    ('c1000000-0000-0000-0000-000000000000', 'client one', 'cp_test_c1', '\x00', '{transfers:write}'),
    ('c2000000-0000-0000-0000-000000000000', 'client two', 'cp_test_c2', '\x00', '{transfers:write}');

INSERT INTO accounts (id, client_id, code, currency, normal_side, debits_must_not_exceed_credits) VALUES
    ('a0000000-0000-0000-0000-00000000000f', 'c1000000-0000-0000-0000-000000000000', 'funding:INR', 'INR', 'debit',  false),
    ('a0000000-0000-0000-0000-00000000000a', 'c1000000-0000-0000-0000-000000000000', 'wallet:alice', 'INR', 'credit', true),
    ('a0000000-0000-0000-0000-00000000000b', 'c1000000-0000-0000-0000-000000000000', 'wallet:bob',   'INR', 'credit', true),
    ('a0000000-0000-0000-0000-0000000000a5', 'c1000000-0000-0000-0000-000000000000', 'wallet:alice:usd', 'USD', 'credit', true),
    ('e0000000-0000-0000-0000-00000000000e', 'c2000000-0000-0000-0000-000000000000', 'wallet:eve',   'INR', 'credit', true);

INSERT INTO account_balances (account_id, debits_must_not_exceed_credits, credits_must_not_exceed_debits)
SELECT id, debits_must_not_exceed_credits, credits_must_not_exceed_debits FROM accounts;

-- t1: funding pays alice 1000.00
BEGIN;
INSERT INTO transactions (id, client_id, kind, currency, reference) VALUES
    ('70000000-0000-0000-0000-000000000001', 'c1000000-0000-0000-0000-000000000000', 'transfer', 'INR', 'fund-alice');
INSERT INTO postings (transaction_id, client_id, currency, account_id, account_seq, direction, amount) VALUES
    ('70000000-0000-0000-0000-000000000001', 'c1000000-0000-0000-0000-000000000000', 'INR', 'a0000000-0000-0000-0000-00000000000f', 1, 'debit',  100000),
    ('70000000-0000-0000-0000-000000000001', 'c1000000-0000-0000-0000-000000000000', 'INR', 'a0000000-0000-0000-0000-00000000000a', 1, 'credit', 100000);
UPDATE account_balances SET debits_posted  = 100000, entry_seq = 1 WHERE account_id = 'a0000000-0000-0000-0000-00000000000f';
UPDATE account_balances SET credits_posted = 100000, entry_seq = 1 WHERE account_id = 'a0000000-0000-0000-0000-00000000000a';
COMMIT;

\warn '--- balanced transactions (CP001)'

SELECT pg_temp.expect_ok('three-leg balanced transfer commits', $sql$
    INSERT INTO transactions (id, client_id, kind, currency, reference) VALUES
        ('70000000-0000-0000-0000-000000000002', 'c1000000-0000-0000-0000-000000000000', 'transfer', 'INR', 'order-1');
    INSERT INTO postings (transaction_id, client_id, currency, account_id, account_seq, direction, amount) VALUES
        ('70000000-0000-0000-0000-000000000002', 'c1000000-0000-0000-0000-000000000000', 'INR', 'a0000000-0000-0000-0000-00000000000a', 2, 'debit',  50000),
        ('70000000-0000-0000-0000-000000000002', 'c1000000-0000-0000-0000-000000000000', 'INR', 'a0000000-0000-0000-0000-00000000000b', 1, 'credit', 48500),
        ('70000000-0000-0000-0000-000000000002', 'c1000000-0000-0000-0000-000000000000', 'INR', 'a0000000-0000-0000-0000-00000000000f', 2, 'credit', 1500);
$sql$);

SELECT pg_temp.expect_error('debits != credits is rejected', 'CP001', $sql$
    INSERT INTO transactions (id, client_id, kind, currency) VALUES
        ('7e000000-0000-0000-0000-000000000001', 'c1000000-0000-0000-0000-000000000000', 'transfer', 'INR');
    INSERT INTO postings (transaction_id, client_id, currency, account_id, account_seq, direction, amount) VALUES
        ('7e000000-0000-0000-0000-000000000001', 'c1000000-0000-0000-0000-000000000000', 'INR', 'a0000000-0000-0000-0000-00000000000a', 90, 'debit',  100),
        ('7e000000-0000-0000-0000-000000000001', 'c1000000-0000-0000-0000-000000000000', 'INR', 'a0000000-0000-0000-0000-00000000000b', 90, 'credit', 90);
$sql$);

SELECT pg_temp.expect_error('transaction with one posting is rejected', 'CP001', $sql$
    INSERT INTO transactions (id, client_id, kind, currency) VALUES
        ('7e000000-0000-0000-0000-000000000002', 'c1000000-0000-0000-0000-000000000000', 'transfer', 'INR');
    INSERT INTO postings (transaction_id, client_id, currency, account_id, account_seq, direction, amount) VALUES
        ('7e000000-0000-0000-0000-000000000002', 'c1000000-0000-0000-0000-000000000000', 'INR', 'a0000000-0000-0000-0000-00000000000a', 90, 'debit', 100);
$sql$);

SELECT pg_temp.expect_error('transaction with no postings is rejected', 'CP001', $sql$
    INSERT INTO transactions (id, client_id, kind, currency) VALUES
        ('7e000000-0000-0000-0000-000000000003', 'c1000000-0000-0000-0000-000000000000', 'transfer', 'INR');
$sql$);

SELECT pg_temp.expect_error('posting added later to a committed transaction is rejected', 'CP001', $sql$
    INSERT INTO postings (transaction_id, client_id, currency, account_id, account_seq, direction, amount) VALUES
        ('70000000-0000-0000-0000-000000000001', 'c1000000-0000-0000-0000-000000000000', 'INR', 'a0000000-0000-0000-0000-00000000000b', 90, 'credit', 1);
$sql$);

\warn '--- append-only (CP002)'

SELECT pg_temp.expect_error('UPDATE postings is rejected', 'CP002',
    $sql$ UPDATE postings SET amount = 1 $sql$);
SELECT pg_temp.expect_error('DELETE postings is rejected', 'CP002',
    $sql$ DELETE FROM postings $sql$);
SELECT pg_temp.expect_error('TRUNCATE postings is rejected', 'CP002',
    $sql$ TRUNCATE postings $sql$);
SELECT pg_temp.expect_error('UPDATE transactions is rejected', 'CP002',
    $sql$ UPDATE transactions SET description = 'edited' $sql$);
SELECT pg_temp.expect_error('DELETE transactions is rejected', 'CP002',
    $sql$ DELETE FROM transactions $sql$);

\warn '--- currency and tenant (composite foreign keys)'

SELECT pg_temp.expect_error('USD account in an INR transaction is rejected', '23503', $sql$
    INSERT INTO transactions (id, client_id, kind, currency) VALUES
        ('7e000000-0000-0000-0000-000000000004', 'c1000000-0000-0000-0000-000000000000', 'transfer', 'INR');
    INSERT INTO postings (transaction_id, client_id, currency, account_id, account_seq, direction, amount) VALUES
        ('7e000000-0000-0000-0000-000000000004', 'c1000000-0000-0000-0000-000000000000', 'INR', 'a0000000-0000-0000-0000-00000000000a', 90, 'debit',  100),
        ('7e000000-0000-0000-0000-000000000004', 'c1000000-0000-0000-0000-000000000000', 'INR', 'a0000000-0000-0000-0000-0000000000a5', 90, 'credit', 100);
$sql$);

SELECT pg_temp.expect_error('another client''s account in a transaction is rejected', '23503', $sql$
    INSERT INTO transactions (id, client_id, kind, currency) VALUES
        ('7e000000-0000-0000-0000-000000000005', 'c1000000-0000-0000-0000-000000000000', 'transfer', 'INR');
    INSERT INTO postings (transaction_id, client_id, currency, account_id, account_seq, direction, amount) VALUES
        ('7e000000-0000-0000-0000-000000000005', 'c1000000-0000-0000-0000-000000000000', 'INR', 'a0000000-0000-0000-0000-00000000000a', 90, 'debit',  100),
        ('7e000000-0000-0000-0000-000000000005', 'c1000000-0000-0000-0000-000000000000', 'INR', 'e0000000-0000-0000-0000-00000000000e', 90, 'credit', 100);
$sql$);

SELECT pg_temp.expect_error('hold across currencies is rejected', '23503', $sql$
    INSERT INTO holds (id, client_id, debit_account_id, credit_account_id, currency, amount, expires_at) VALUES
        ('b0000000-0000-0000-0000-000000000009','c1000000-0000-0000-0000-000000000000',
         'a0000000-0000-0000-0000-00000000000a', 'a0000000-0000-0000-0000-0000000000a5', 'INR', 100, now() + interval '1 hour');
$sql$);

\warn '--- overdraft rules (CHECK on account_balances)'

SELECT pg_temp.expect_error('posted debits above credits on a wallet are rejected', '23514',
    $sql$ UPDATE account_balances SET debits_posted = 100001 WHERE account_id = 'a0000000-0000-0000-0000-00000000000a' $sql$);
SELECT pg_temp.expect_error('pending debits counting against available balance', '23514',
    $sql$ UPDATE account_balances SET debits_pending = 100001 WHERE account_id = 'a0000000-0000-0000-0000-00000000000a' $sql$);
SELECT pg_temp.expect_ok('system account without a flag may go negative',
    $sql$ UPDATE account_balances SET credits_posted = 999999 WHERE account_id = 'a0000000-0000-0000-0000-00000000000f' $sql$);
SELECT pg_temp.expect_error('turning off an overdraft flag after creation is rejected', '23503',
    $sql$ UPDATE accounts SET debits_must_not_exceed_credits = false WHERE id = 'a0000000-0000-0000-0000-00000000000a' $sql$);

\warn '--- per-account sequence and leg uniqueness'

SELECT pg_temp.expect_error('duplicate account_seq is rejected', '23505', $sql$
    INSERT INTO transactions (id, client_id, kind, currency) VALUES
        ('7e000000-0000-0000-0000-000000000006', 'c1000000-0000-0000-0000-000000000000', 'transfer', 'INR');
    INSERT INTO postings (transaction_id, client_id, currency, account_id, account_seq, direction, amount) VALUES
        ('7e000000-0000-0000-0000-000000000006', 'c1000000-0000-0000-0000-000000000000', 'INR', 'a0000000-0000-0000-0000-00000000000a', 1,  'debit',  100),
        ('7e000000-0000-0000-0000-000000000006', 'c1000000-0000-0000-0000-000000000000', 'INR', 'a0000000-0000-0000-0000-00000000000b', 90, 'credit', 100);
$sql$);

SELECT pg_temp.expect_error('same account twice in one transaction is rejected', '23505', $sql$
    INSERT INTO transactions (id, client_id, kind, currency) VALUES
        ('7e000000-0000-0000-0000-000000000007', 'c1000000-0000-0000-0000-000000000000', 'transfer', 'INR');
    INSERT INTO postings (transaction_id, client_id, currency, account_id, account_seq, direction, amount) VALUES
        ('7e000000-0000-0000-0000-000000000007', 'c1000000-0000-0000-0000-000000000000', 'INR', 'a0000000-0000-0000-0000-00000000000a', 90, 'debit',  100),
        ('7e000000-0000-0000-0000-000000000007', 'c1000000-0000-0000-0000-000000000000', 'INR', 'a0000000-0000-0000-0000-00000000000a', 91, 'credit', 100);
$sql$);

SELECT pg_temp.expect_error('duplicate transfer reference is rejected', '23505', $sql$
    INSERT INTO transactions (id, client_id, kind, currency, reference) VALUES
        ('7e000000-0000-0000-0000-000000000008', 'c1000000-0000-0000-0000-000000000000', 'transfer', 'INR', 'order-1');
$sql$);

\warn '--- reversals'

SELECT pg_temp.expect_ok('reversing a transfer commits', $sql$
    INSERT INTO transactions (id, client_id, kind, currency, reverses_transaction_id) VALUES
        ('70000000-0000-0000-0000-000000000003', 'c1000000-0000-0000-0000-000000000000', 'reversal', 'INR', '70000000-0000-0000-0000-000000000002');
    INSERT INTO postings (transaction_id, client_id, currency, account_id, account_seq, direction, amount) VALUES
        ('70000000-0000-0000-0000-000000000003', 'c1000000-0000-0000-0000-000000000000', 'INR', 'a0000000-0000-0000-0000-00000000000a', 3, 'credit', 50000),
        ('70000000-0000-0000-0000-000000000003', 'c1000000-0000-0000-0000-000000000000', 'INR', 'a0000000-0000-0000-0000-00000000000b', 2, 'debit',  48500),
        ('70000000-0000-0000-0000-000000000003', 'c1000000-0000-0000-0000-000000000000', 'INR', 'a0000000-0000-0000-0000-00000000000f', 3, 'debit',  1500);
$sql$);

SELECT pg_temp.expect_error('second reversal of the same transaction is rejected', '23505', $sql$
    INSERT INTO transactions (id, client_id, kind, currency, reverses_transaction_id) VALUES
        ('7e000000-0000-0000-0000-000000000009', 'c1000000-0000-0000-0000-000000000000', 'reversal', 'INR', '70000000-0000-0000-0000-000000000002');
$sql$);

SELECT pg_temp.expect_error('reversing a reversal is rejected', 'CP004', $sql$
    INSERT INTO transactions (id, client_id, kind, currency, reverses_transaction_id) VALUES
        ('7e000000-0000-0000-0000-00000000000a', 'c1000000-0000-0000-0000-000000000000', 'reversal', 'INR', '70000000-0000-0000-0000-000000000003');
$sql$);

SELECT pg_temp.expect_error('reversal without a target is rejected', '23514', $sql$
    INSERT INTO transactions (id, client_id, kind, currency) VALUES
        ('7e000000-0000-0000-0000-00000000000b', 'c1000000-0000-0000-0000-000000000000', 'reversal', 'INR');
$sql$);

SELECT pg_temp.expect_error('reversing another client''s transaction is rejected', '23503', $sql$
    INSERT INTO transactions (id, client_id, kind, currency, reverses_transaction_id) VALUES
        ('7e000000-0000-0000-0000-00000000000c', 'c2000000-0000-0000-0000-000000000000', 'reversal', 'INR', '70000000-0000-0000-0000-000000000001');
$sql$);

\warn '--- holds (CP003 and CHECKs)'

INSERT INTO holds (id, client_id, debit_account_id, credit_account_id, currency, amount, expires_at) VALUES
    ('b0000000-0000-0000-0000-000000000001', 'c1000000-0000-0000-0000-000000000000',
     'a0000000-0000-0000-0000-00000000000a', 'a0000000-0000-0000-0000-00000000000b', 'INR', 10000, now() + interval '1 hour'),
    ('b0000000-0000-0000-0000-000000000002', 'c1000000-0000-0000-0000-000000000000',
     'a0000000-0000-0000-0000-00000000000a', 'a0000000-0000-0000-0000-00000000000b', 'INR', 10000, now() + interval '1 hour');

SELECT pg_temp.expect_error('capture without a hold is rejected', '23514', $sql$
    INSERT INTO transactions (id, client_id, kind, currency) VALUES
        ('7e000000-0000-0000-0000-00000000000d', 'c1000000-0000-0000-0000-000000000000', 'capture', 'INR');
$sql$);

SELECT pg_temp.expect_ok('partial capture commits', $sql$
    UPDATE holds SET captured_amount = 4000, status = 'partially_captured' WHERE id = 'b0000000-0000-0000-0000-000000000001';
    INSERT INTO transactions (id, client_id, kind, currency, hold_id) VALUES
        ('70000000-0000-0000-0000-000000000004', 'c1000000-0000-0000-0000-000000000000', 'capture', 'INR', 'b0000000-0000-0000-0000-000000000001');
    INSERT INTO postings (transaction_id, client_id, currency, account_id, account_seq, direction, amount) VALUES
        ('70000000-0000-0000-0000-000000000004', 'c1000000-0000-0000-0000-000000000000', 'INR', 'a0000000-0000-0000-0000-00000000000a', 4, 'debit',  4000),
        ('70000000-0000-0000-0000-000000000004', 'c1000000-0000-0000-0000-000000000000', 'INR', 'a0000000-0000-0000-0000-00000000000b', 3, 'credit', 4000);
$sql$);

SELECT pg_temp.expect_error('capturing more than the hold amount is rejected', '23514',
    $sql$ UPDATE holds SET captured_amount = 10001 WHERE id = 'b0000000-0000-0000-0000-000000000001' $sql$);
SELECT pg_temp.expect_error('status captured with an uncaptured remainder is rejected', '23514',
    $sql$ UPDATE holds SET status = 'captured' WHERE id = 'b0000000-0000-0000-0000-000000000001' $sql$);
SELECT pg_temp.expect_error('captured_amount decreasing is rejected', 'CP003',
    $sql$ UPDATE holds SET captured_amount = 1000 WHERE id = 'b0000000-0000-0000-0000-000000000001' $sql$);
SELECT pg_temp.expect_error('partially captured hold returning to pending is rejected', 'CP003',
    $sql$ UPDATE holds SET status = 'pending', captured_amount = 4000 WHERE id = 'b0000000-0000-0000-0000-000000000001' $sql$);
SELECT pg_temp.expect_error('changing hold terms is rejected', 'CP003',
    $sql$ UPDATE holds SET amount = 20000 WHERE id = 'b0000000-0000-0000-0000-000000000001' $sql$);
SELECT pg_temp.expect_ok('partially captured hold can expire',
    $sql$ UPDATE holds SET status = 'expired' WHERE id = 'b0000000-0000-0000-0000-000000000001' $sql$);
SELECT pg_temp.expect_error('expired hold cannot change', 'CP003',
    $sql$ UPDATE holds SET status = 'voided' WHERE id = 'b0000000-0000-0000-0000-000000000001' $sql$);
SELECT pg_temp.expect_ok('pending hold can be voided',
    $sql$ UPDATE holds SET status = 'voided' WHERE id = 'b0000000-0000-0000-0000-000000000002' $sql$);
SELECT pg_temp.expect_error('holds cannot be deleted', 'CP003',
    $sql$ DELETE FROM holds $sql$);

\warn '--- idempotency'

SELECT pg_temp.expect_error('response body without a response code is rejected', '23514', $sql$
    INSERT INTO idempotency_keys (client_id, key, request_method, request_path, request_hash, response_body) VALUES
        ('c1000000-0000-0000-0000-000000000000', 'k1', 'POST', '/v1/transfers', '\x00', '{}');
$sql$);

\warn 'all guard tests passed'
