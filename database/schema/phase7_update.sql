-- Run as postgres on imaging_ai_db. Save a pre-change owner, direct ACL and
-- effective ACL baseline first. Use one session and review before COMMIT.
BEGIN;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '60s';
DO $phase7$
DECLARE
    account regclass;
    col record;
    constraint_row record;
    owner_name text;
BEGIN

    IF current_database() <> 'imaging_ai_db' OR to_regrole('imaging_ai_user') IS NULL THEN
        RAISE EXCEPTION 'Connect to imaging_ai_db with imaging_ai_user already provisioned';
    END IF;
    PERFORM set_config('search_path', 'pg_catalog', true);
    account := to_regclass('system.user_account');

    IF current_user <> 'postgres' OR NOT (SELECT rolsuper FROM pg_roles WHERE rolname = current_user)
        THEN RAISE EXCEPTION 'Run the update as postgres with maintenance privileges'; END IF;
    SELECT pg_get_userbyid(datdba) INTO owner_name FROM pg_database WHERE datname = current_database();
    IF owner_name NOT IN ('postgres','imaging_ai_user') OR
       (SELECT count(*) FROM pg_namespace WHERE
        (nspname IN ('clinical','system') AND pg_get_userbyid(nspowner) IN ('postgres','imaging_ai_user'))
        OR (nspname = 'public' AND nspowner = 'pg_database_owner'::regrole)) <> 3
    THEN RAISE EXCEPTION 'Unexpected database or schema owners'; END IF;

    IF (SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname IN ('clinical','system') AND c.relkind IN ('r','p')) <> 17
       OR (SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
           WHERE n.nspname IN ('clinical','system') AND c.relkind = 'S') <> 16
       OR EXISTS (SELECT 1 FROM unnest(ARRAY[
          'clinical.case_result','clinical.clinical_report','clinical.observation_result',
          'clinical.patient','clinical.practitioner','clinical.report_practitioner',
          'clinical.report_section','clinical.report_type','clinical.result_evidence',
          'system.audit_trail','system.exception_queue','system.ocr_candidate_batch',
          'system.processing_log','system.review_record','system.role','system.structured_output',
          'system.user_account']) AS expected(name)
          WHERE NOT EXISTS (SELECT 1 FROM pg_class WHERE oid = to_regclass(expected.name)
                             AND relkind IN ('r','p') AND relowner = 'postgres'::regrole))
    THEN RAISE EXCEPTION 'Unexpected Phase 2 relation inventory or table ownership'; END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = account
                   AND attname = 'secondary_role_id' AND atttypid = 'bigint'::regtype
                   AND NOT attnotnull AND NOT attisdropped)
       OR (SELECT count(*) FROM pg_constraint WHERE conrelid = account AND convalidated AND
           ((conname = 'fk_user_secondary_role' AND contype = 'f' AND
             pg_get_constraintdef(oid) = 'FOREIGN KEY (secondary_role_id) REFERENCES system.role(role_id)')
            OR (conname = 'ck_user_account_distinct_roles' AND contype = 'c' AND
                regexp_replace(pg_get_expr(conbin, conrelid), '[()[:space:]]', '', 'g') =
                  'secondary_role_idISNULLORsecondary_role_id<>role_id'))) <> 2
       OR (SELECT count(*) FROM pg_attribute WHERE attrelid = account AND attnum > 0
           AND NOT attisdropped AND attname NOT IN ('email','email_verified_at')) <> 7
       OR (SELECT count(*) FROM pg_attribute WHERE attrelid = account AND attnum > 0
           AND NOT attisdropped AND attname IN
           ('user_id','username','password_hash','role_id','secondary_role_id','is_active','created_at')) <> 7
    THEN RAISE EXCEPTION 'Phase 2 user_account baseline mismatch'; END IF;

    FOR col IN SELECT attname, format_type(atttypid, atttypmod) AS data_type,
                      attnotnull, atthasdef, attidentity, attgenerated
               FROM pg_attribute WHERE attrelid = account AND NOT attisdropped
               AND attname IN ('email','email_verified_at')
    LOOP
        IF col.attnotnull OR col.atthasdef OR col.attidentity <> '' OR col.attgenerated <> ''
           OR col.data_type <> CASE WHEN col.attname = 'email' THEN 'character varying(254)'
                                    ELSE 'timestamp with time zone' END
        THEN RAISE EXCEPTION 'Incompatible column %', col.attname; END IF;
    END LOOP;

    FOR constraint_row IN SELECT conname, contype, convalidated, connoinherit,
                                  pg_get_expr(conbin, conrelid) AS expression
                           FROM pg_constraint WHERE conrelid = account
                           AND conname IN ('ck_user_account_email_nonblank',
                                           'ck_user_account_email_verified_requires_email')
    LOOP
        IF constraint_row.contype <> 'c' OR NOT constraint_row.convalidated
           OR constraint_row.connoinherit
           OR regexp_replace(constraint_row.expression, '[()[:space:]]', '', 'g')
              IS DISTINCT FROM CASE WHEN constraint_row.conname = 'ck_user_account_email_nonblank'
                THEN 'emailISNULLORlengthemail::text>0ANDemail::text!~''[[:space:][:cntrl:]]''::text'
                ELSE 'email_verified_atISNULLORemailISNOTNULL' END
        THEN RAISE EXCEPTION 'Incompatible constraint %', constraint_row.conname; END IF;
    END LOOP;

    IF to_regclass('system.ux_user_account_email_normalized') IS NOT NULL AND NOT EXISTS (
        SELECT 1 FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid
        JOIN pg_am am ON am.oid = c.relam
        WHERE i.indexrelid = to_regclass('system.ux_user_account_email_normalized')
          AND i.indrelid = account AND am.amname = 'btree'
          AND i.indisunique AND i.indisvalid AND i.indisready AND i.indislive
          AND i.indimmediate AND NOT i.indnullsnotdistinct
          AND i.indnkeyatts = 1 AND i.indnatts = 1
          AND pg_get_indexdef(i.indexrelid,1,true) IN ('lower(email::text)','lower((email)::text)','lower(email)')
          AND pg_get_expr(i.indpred,i.indrelid) = '(email IS NOT NULL)'
          AND pg_get_indexdef(i.indexrelid) ~ ' USING btree \(lower\(\(?email\)?(::text)?\)\) WHERE \(email IS NOT NULL\)$'
    ) THEN RAISE EXCEPTION 'Incompatible normalized email index'; END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = account AND attname = 'email' AND NOT attisdropped) THEN
        ALTER TABLE system.user_account ADD COLUMN email character varying(254);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = account AND attname = 'email_verified_at' AND NOT attisdropped) THEN
        ALTER TABLE system.user_account ADD COLUMN email_verified_at timestamp with time zone;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = account AND conname = 'ck_user_account_email_nonblank') THEN
        ALTER TABLE system.user_account ADD CONSTRAINT ck_user_account_email_nonblank CHECK (
            email IS NULL OR (length(email) > 0 AND email !~ '[[:space:][:cntrl:]]'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = account AND conname = 'ck_user_account_email_verified_requires_email') THEN
        ALTER TABLE system.user_account ADD CONSTRAINT ck_user_account_email_verified_requires_email
            CHECK (email_verified_at IS NULL OR email IS NOT NULL);
    END IF;
    IF to_regclass('system.ux_user_account_email_normalized') IS NULL THEN
        CREATE UNIQUE INDEX ux_user_account_email_normalized ON system.user_account (lower(email)) WHERE email IS NOT NULL;
    END IF;

    ALTER DATABASE imaging_ai_db OWNER TO postgres;
    ALTER SCHEMA clinical OWNER TO postgres;
    ALTER SCHEMA system OWNER TO postgres;
    GRANT CONNECT ON DATABASE imaging_ai_db TO imaging_ai_user;
    GRANT USAGE ON SCHEMA clinical, system, public TO imaging_ai_user;
    REVOKE CREATE ON SCHEMA clinical, system, public FROM imaging_ai_user;
    REVOKE ALL PRIVILEGES ON TABLE system.user_account, system.role FROM imaging_ai_user;
    FOR col IN SELECT r.id, string_agg(quote_ident(a.attname), ', ' ORDER BY a.attnum) AS column_list
               FROM unnest(ARRAY[account,'system.role'::regclass]) AS r(id)
               JOIN pg_attribute a ON a.attrelid = r.id
               WHERE a.attnum > 0 AND NOT a.attisdropped GROUP BY r.id
    LOOP
        EXECUTE format('REVOKE ALL PRIVILEGES (%s) ON TABLE %s FROM imaging_ai_user', col.column_list, col.id);
    END LOOP;
    GRANT SELECT (user_id, username, password_hash, role_id, secondary_role_id,
                  is_active, created_at, email, email_verified_at)
        ON system.user_account TO imaging_ai_user;
    GRANT SELECT ON system.role TO imaging_ai_user;
    REVOKE TRUNCATE ON ALL TABLES IN SCHEMA clinical, system FROM imaging_ai_user;

    IF (SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname = current_database()) <> 'postgres'
       OR (SELECT count(*) FROM pg_namespace WHERE
           (nspname IN ('clinical','system') AND nspowner = 'postgres'::regrole)
           OR (nspname = 'public' AND nspowner = 'pg_database_owner'::regrole)) <> 3
       OR NOT has_database_privilege('imaging_ai_user', current_database(), 'CONNECT')
       OR EXISTS (SELECT 1 FROM unnest(ARRAY['clinical','system','public']) AS s(name)
                  WHERE NOT has_schema_privilege('imaging_ai_user', name, 'USAGE')
                     OR has_schema_privilege('imaging_ai_user', name, 'CREATE'))
       OR has_table_privilege('imaging_ai_user', account, 'SELECT')
       OR NOT has_table_privilege('imaging_ai_user', 'system.role', 'SELECT')
       OR EXISTS (SELECT 1 FROM unnest(ARRAY[account,'system.role'::regclass]) AS r(id)
                  CROSS JOIN unnest(ARRAY['INSERT','UPDATE','DELETE','TRUNCATE','REFERENCES','TRIGGER']) AS p(privilege)
                  WHERE has_table_privilege('imaging_ai_user', r.id, p.privilege))
       OR EXISTS (SELECT 1 FROM pg_attribute a
                  WHERE a.attrelid IN (account,'system.role'::regclass) AND a.attnum > 0 AND NOT a.attisdropped
                  AND (NOT has_column_privilege('imaging_ai_user', a.attrelid, a.attnum, 'SELECT')
                       OR has_column_privilege('imaging_ai_user', a.attrelid, a.attnum, 'INSERT')
                       OR has_column_privilege('imaging_ai_user', a.attrelid, a.attnum, 'UPDATE')
                       OR has_column_privilege('imaging_ai_user', a.attrelid, a.attnum, 'REFERENCES')))
       OR EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON c.relnamespace = n.oid
                  WHERE n.nspname IN ('clinical','system') AND c.relkind IN ('r','p')
                    AND has_table_privilege('imaging_ai_user', c.oid, 'TRUNCATE'))
    THEN RAISE EXCEPTION 'Effective owner or ACL mismatch (check inherited and PUBLIC grants)'; END IF;

    IF (SELECT count(*) FROM pg_attribute WHERE attrelid = account AND attnum > 0 AND NOT attisdropped
        AND attname IN ('email','email_verified_at')) <> 2
       OR (SELECT count(*) FROM pg_constraint WHERE conrelid = account AND
           conname IN ('ck_user_account_email_nonblank','ck_user_account_email_verified_requires_email')) <> 2
       OR to_regclass('system.ux_user_account_email_normalized') IS NULL
    THEN RAISE EXCEPTION 'Missing Phase 7 email objects'; END IF;

END
$phase7$;
SELECT 'Owner, effective ACL and required Email objects verified within this transaction' AS result;
-- Review the result, then run COMMIT; or ROLLBACK; on this SAME connection.
