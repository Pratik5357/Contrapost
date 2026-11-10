-- 005: retention cleanup (design doc §10) and a least-privilege role for the services (§11).

-- The daily outbox cleanup deletes published rows by age; this keeps that scan off the whole table.
CREATE INDEX ix_outbox_published ON outbox_messages (published_at) WHERE published_at IS NOT NULL;

-- The API, Worker and Consumers log in as members of contrapost_app; migrations keep running as the
-- owner. The role can read and write everything it needs, but postings and transactions only take
-- INSERT and SELECT, so history stays append-only even if a trigger were disabled.
-- Roles are cluster-wide, so another database in the same cluster may have created it already.
DO $$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'contrapost_app') THEN
        CREATE ROLE contrapost_app NOLOGIN;
    END IF;
END $$;

GRANT USAGE ON SCHEMA public TO contrapost_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO contrapost_app;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO contrapost_app;
REVOKE UPDATE, DELETE ON postings, transactions FROM contrapost_app;

-- Tables and sequences that later migrations create (as the same owner) get the same grants.
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO contrapost_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO contrapost_app;
