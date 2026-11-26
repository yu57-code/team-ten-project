-- Run as the schema/table owner in the pgAdmin Query Tool after backup and approval.
BEGIN;
SET LOCAL lock_timeout = '5s';
SET LOCAL search_path = pg_catalog;

DO $phase2$
DECLARE
    account_columns text;
    role_columns text;
    secondary_column text;
    fk_definition text;
    check_definition text;
BEGIN
    IF current_database() <> 'imaging_ai_db'
       OR to_regrole('imaging_ai_user') IS NULL
       OR (SELECT count(*) FROM pg_tables WHERE schemaname IN ('clinical', 'system')) <> 17
       OR to_regclass('system.user_account') IS NULL OR to_regclass('system.role') IS NULL THEN
        RAISE EXCEPTION 'Unexpected database, application role, or base tables';
    END IF;

    LOCK TABLE system.role IN SHARE MODE;
    LOCK TABLE system.user_account IN ACCESS EXCLUSIVE MODE;

    SELECT string_agg(format('%s:%s:%s:%s', attname, format_type(atttypid, atttypmod),
                             attnotnull, attidentity), ',' ORDER BY attname)
      INTO account_columns
      FROM pg_attribute
     WHERE attrelid = 'system.user_account'::regclass AND attnum > 0 AND NOT attisdropped
       AND attname <> 'secondary_role_id';
    SELECT string_agg(format('%s:%s:%s:%s', attname, format_type(atttypid, atttypmod),
                             attnotnull, attidentity), ',' ORDER BY attname)
      INTO role_columns
      FROM pg_attribute
     WHERE attrelid = 'system.role'::regclass AND attnum > 0 AND NOT attisdropped;
    IF account_columns IS DISTINCT FROM
       'created_at:timestamp with time zone:t:,is_active:boolean:t:,password_hash:text:t:,role_id:bigint:t:,user_id:bigint:t:d,username:character varying(100):t:'
       OR role_columns IS DISTINCT FROM
          'role_code:character varying(50):t:,role_id:bigint:t:d,role_name:character varying(100):t:'
       OR (SELECT pg_get_expr(d.adbin, d.adrelid)
             FROM pg_attrdef d JOIN pg_attribute a ON a.attrelid = d.adrelid AND a.attnum = d.adnum
            WHERE d.adrelid = 'system.user_account'::regclass AND a.attname = 'created_at')
          IS DISTINCT FROM 'CURRENT_TIMESTAMP'
       OR (SELECT pg_get_expr(d.adbin, d.adrelid)
             FROM pg_attrdef d JOIN pg_attribute a ON a.attrelid = d.adrelid AND a.attnum = d.adnum
            WHERE d.adrelid = 'system.user_account'::regclass AND a.attname = 'is_active')
          IS DISTINCT FROM 'true'
       OR (SELECT count(*) FROM pg_constraint
            WHERE conrelid = 'system.user_account'::regclass AND convalidated
              AND pg_get_constraintdef(oid) IN ('PRIMARY KEY (user_id)', 'UNIQUE (username)',
                                               'FOREIGN KEY (role_id) REFERENCES system.role(role_id)')) <> 3
       OR (SELECT count(*) FROM pg_constraint
            WHERE conrelid = 'system.role'::regclass AND convalidated
              AND pg_get_constraintdef(oid) IN ('PRIMARY KEY (role_id)', 'UNIQUE (role_code)')) <> 2
       OR (SELECT array_agg(role_code::text ORDER BY role_code COLLATE "C") FROM system.role)
          IS DISTINCT FROM ARRAY['MEDICAL_REVIEWER', 'SYSTEM_ADMIN']
       OR NOT has_column_privilege('imaging_ai_user', 'system.user_account', 'user_id', 'SELECT')
       OR NOT has_column_privilege('imaging_ai_user', 'system.user_account', 'is_active', 'SELECT')
       OR NOT EXISTS (SELECT 1 FROM system.user_account WHERE username = 'dev_uploader') THEN
        RAISE EXCEPTION 'Baseline mismatch in account structure, roles, grants, or dev_uploader';
    END IF;

    SELECT format('%s:%s:%s:%s:%s', format_type(atttypid, atttypmod),
                  attnotnull, atthasdef, attidentity, attgenerated)
      INTO secondary_column
      FROM pg_attribute
     WHERE attrelid = 'system.user_account'::regclass AND attnum > 0 AND NOT attisdropped
       AND attname = 'secondary_role_id';
    SELECT CASE WHEN convalidated THEN pg_get_constraintdef(oid) END INTO fk_definition
      FROM pg_constraint WHERE conrelid = 'system.user_account'::regclass
       AND conname = 'fk_user_secondary_role';
    SELECT CASE WHEN convalidated AND NOT connoinherit
                THEN regexp_replace(lower(pg_get_constraintdef(oid)), '[[:space:]()]', '', 'g')
           END INTO check_definition
      FROM pg_constraint WHERE conrelid = 'system.user_account'::regclass
       AND conname = 'ck_user_account_distinct_roles';

    IF secondary_column IS NULL THEN
        IF EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'system.user_account'::regclass
                    AND conname IN ('fk_user_secondary_role', 'ck_user_account_distinct_roles')) THEN
            RAISE EXCEPTION 'Unexpected existing Phase 2 constraint';
        END IF;
        ALTER TABLE system.user_account
            ADD COLUMN secondary_role_id bigint,
            ADD CONSTRAINT fk_user_secondary_role
                FOREIGN KEY (secondary_role_id) REFERENCES system.role(role_id),
            ADD CONSTRAINT ck_user_account_distinct_roles
                CHECK (secondary_role_id IS NULL OR secondary_role_id <> role_id);
    ELSIF secondary_column <> 'bigint:f:f::'
       OR fk_definition IS DISTINCT FROM 'FOREIGN KEY (secondary_role_id) REFERENCES system.role(role_id)'
       OR check_definition IS DISTINCT FROM 'checksecondary_role_idisnullorsecondary_role_id<>role_id' THEN
        RAISE EXCEPTION 'Existing secondary role does not match Phase 2';
    END IF;
END;
$phase2$;

GRANT USAGE ON SCHEMA system TO imaging_ai_user;
GRANT SELECT (username, password_hash, role_id, secondary_role_id)
    ON system.user_account TO imaging_ai_user;
GRANT SELECT (role_id, role_code) ON system.role TO imaging_ai_user;
COMMIT;
