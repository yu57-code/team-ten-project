-- Run as an authorized database maintainer before and after the update.
BEGIN READ ONLY;
SELECT current_database() AS database_name, current_user AS inspected_by,
       (SELECT count(*) FROM pg_tables WHERE schemaname IN ('clinical', 'system')) AS table_count,
       to_regrole('imaging_ai_user') IS NOT NULL AS app_role_exists;
SELECT schemaname, tablename FROM pg_tables
WHERE schemaname IN ('clinical', 'system') ORDER BY schemaname, tablename;

SELECT table_name, column_name, data_type, character_maximum_length,
       is_nullable, column_default, identity_generation
FROM information_schema.columns
WHERE table_schema = 'system' AND table_name IN ('role', 'user_account')
ORDER BY table_name, ordinal_position;
SELECT conrelid::regclass AS relation, conname, convalidated, pg_get_constraintdef(oid) AS definition
FROM pg_constraint
WHERE conrelid IN ('system.role'::regclass, 'system.user_account'::regclass)
   OR confrelid = 'system.user_account'::regclass
ORDER BY relation, conname;
SELECT indexname, indexdef FROM pg_indexes
WHERE schemaname = 'system' AND tablename IN ('role', 'user_account') ORDER BY indexname;

SELECT has_schema_privilege(to_regrole('imaging_ai_user'), 'system', 'USAGE') AS system_usage;
SELECT a.attrelid::regclass AS relation, a.attname AS column_name,
       has_column_privilege(to_regrole('imaging_ai_user'), a.attrelid, a.attnum, 'SELECT') AS can_select,
       has_column_privilege(to_regrole('imaging_ai_user'), a.attrelid, a.attnum, 'INSERT') AS can_insert,
       has_column_privilege(to_regrole('imaging_ai_user'), a.attrelid, a.attnum, 'UPDATE') AS can_update,
       a.attacl AS direct_column_acl
FROM pg_attribute a
WHERE a.attrelid IN ('system.role'::regclass, 'system.user_account'::regclass)
  AND a.attnum > 0 AND NOT a.attisdropped ORDER BY relation, a.attnum;
SELECT nspacl AS direct_schema_acl FROM pg_namespace WHERE nspname = 'system';
SELECT role_id, role_code FROM system.role ORDER BY role_code;
SELECT u.user_id, u.username, r.role_code, u.is_active
FROM system.user_account u JOIN system.role r ON r.role_id = u.role_id
WHERE u.username = 'dev_uploader';
SELECT count(*) AS report_count FROM clinical.clinical_report;

-- These account checks appear on the pgAdmin Messages tab after the column exists.
DO $verify$
DECLARE
    account record;
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = 'system.user_account'::regclass
                    AND attname = 'secondary_role_id' AND attnum > 0 AND NOT attisdropped) THEN
        RAISE NOTICE 'secondary_role_id is absent (baseline)';
        RETURN;
    END IF;
    FOR account IN
        WITH expected(username, primary_code, secondary_code, is_active) AS (
            VALUES ('413570452', 'MEDICAL_REVIEWER', 'SYSTEM_ADMIN', true),
                   ('413570438', 'SYSTEM_ADMIN', NULL, true),
                   ('413570359', 'MEDICAL_REVIEWER', NULL, true),
                   ('413570309', 'MEDICAL_REVIEWER', NULL, false)
        )
        SELECT e.username, u.user_id IS NOT NULL AS present, r.role_code AS primary_code,
               s.role_code AS secondary_code, u.is_active,
               u.user_id IS NOT NULL AND r.role_code = e.primary_code
                 AND s.role_code IS NOT DISTINCT FROM e.secondary_code
                 AND u.is_active = e.is_active AS matches
        FROM expected e LEFT JOIN system.user_account u USING (username)
        LEFT JOIN system.role r ON r.role_id = u.role_id
        LEFT JOIN system.role s ON s.role_id = u.secondary_role_id
        ORDER BY e.username
    LOOP
        RAISE NOTICE '% present=% primary=% secondary=% active=% matches=%',
            account.username, account.present, account.primary_code,
            account.secondary_code, account.is_active, account.matches;
    END LOOP;
END;
$verify$;
ROLLBACK;
