-- 004: the W3C trace context of the request that wrote each outbox row (design doc §12), so the relay
-- and the consumers continue that trace and one transfer can be followed end to end.
ALTER TABLE outbox_messages ADD COLUMN traceparent text;
