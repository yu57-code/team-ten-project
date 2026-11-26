-- Run as the same grantor/owner after backup, review, and separate rollback approval.
BEGIN;
SET LOCAL lock_timeout = '5s';
SET LOCAL search_path = pg_catalog;

DO $rollback$
DECLARE
    -- List only direct grants absent in this machine's saved baseline and added by Phase 2.
    -- Allowed: system_usage, username, password_hash, account_role_id, role_id, role_code.
    revoke_phase2 text[] := ARRAY[]::text[];
    grant_key text;
BEGIN
    IF current_database() <> 'imaging_ai_db' THEN
        RAISE EXCEPTION 'Wrong database for Phase 2 rollback';
    END IF;
    LOCK TABLE system.user_account IN ACCESS EXCLUSIVE MODE;
    IF NOT EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = 'system.user_account'::regclass
                    AND attname = 'secondary_role_id' AND attnum > 0 AND NOT attisdropped) THEN
        RAISE EXCEPTION 'Phase 2 column is absent';
    END IF;
    IF (SELECT count(*) FROM pg_constraint WHERE conrelid = 'system.user_account'::regclass
         AND conname IN ('fk_user_secondary_role', 'ck_user_account_distinct_roles')) <> 2 THEN
        RAISE EXCEPTION 'Unexpected Phase 2 constraints';
    END IF;
    IF EXISTS (SELECT 1 FROM system.user_account WHERE secondary_role_id IS NOT NULL) THEN
        RAISE EXCEPTION 'Secondary roles still assigned; review each assignment before rollback';
    END IF;

    ALTER TABLE system.user_account
        DROP CONSTRAINT fk_user_secondary_role,
        DROP CONSTRAINT ck_user_account_distinct_roles,
        DROP COLUMN secondary_role_id RESTRICT;

    FOREACH grant_key IN ARRAY revoke_phase2 LOOP
        CASE grant_key
            WHEN 'system_usage' THEN REVOKE USAGE ON SCHEMA system FROM imaging_ai_user RESTRICT;
            WHEN 'username' THEN REVOKE SELECT (username) ON system.user_account FROM imaging_ai_user RESTRICT;
            WHEN 'password_hash' THEN REVOKE SELECT (password_hash) ON system.user_account FROM imaging_ai_user RESTRICT;
            WHEN 'account_role_id' THEN REVOKE SELECT (role_id) ON system.user_account FROM imaging_ai_user RESTRICT;
            WHEN 'role_id' THEN REVOKE SELECT (role_id) ON system.role FROM imaging_ai_user RESTRICT;
            WHEN 'role_code' THEN REVOKE SELECT (role_code) ON system.role FROM imaging_ai_user RESTRICT;
            ELSE RAISE EXCEPTION 'Unknown rollback grant key: %', grant_key;
        END CASE;
    END LOOP;
END;
$rollback$;
COMMIT;
