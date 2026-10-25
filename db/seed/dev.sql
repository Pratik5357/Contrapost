-- Local demo data. Safe to run repeatedly. Applied by scripts/seed.sh.
-- Until API keys exist (M2), requests authenticate with: X-Client-Id: 11111111-1111-1111-1111-111111111111
INSERT INTO api_clients (id, name, key_prefix, key_hash, scopes)
VALUES ('11111111-1111-1111-1111-111111111111', 'demo', 'cp_dev_demo', '\x00',
        '{accounts:read,accounts:write,transfers:write,holds:write,admin:read}')
ON CONFLICT (id) DO NOTHING;
