-- 003: indexes for the reconciliation job (design doc §8.3, §10), which checks every account's
-- pending totals against its open holds (I5) and every transaction's outbox message (I7).
CREATE INDEX ix_holds_open_debit  ON holds (debit_account_id)  WHERE status IN ('pending', 'partially_captured');
CREATE INDEX ix_holds_open_credit ON holds (credit_account_id) WHERE status IN ('pending', 'partially_captured');
CREATE INDEX ix_outbox_aggregate  ON outbox_messages (aggregate_id);
