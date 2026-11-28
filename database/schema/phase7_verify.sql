-- Run on a fresh connection after commit. This script never reads account values.
BEGIN READ ONLY;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '60s';
DO $phase7$
DECLARE
    account regclass;
    col record;
    constraint_row record;
BEGIN

    IF current_database() <> 'imaging_ai_db' OR to_regrole('imaging_ai_user') IS NULL THEN
        RAISE EXCEPTION 'Connect to imaging_ai_db with imaging_ai_user already provisioned';
    END IF;
    PERFORM set_config('search_path', 'pg_catalog', true);
    account := to_regclass('system.user_account');

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
SELECT n.nspname, c.relname, c.relkind, pg_get_userbyid(c.relowner) AS owner,
       CASE WHEN c.relkind = 'S' THEN jsonb_build_object(
           'usage', has_sequence_privilege('imaging_ai_user', c.oid, 'USAGE'),
           'select', has_sequence_privilege('imaging_ai_user', c.oid, 'SELECT'),
           'update', has_sequence_privilege('imaging_ai_user', c.oid, 'UPDATE'))
       ELSE jsonb_build_object(
           'select', has_table_privilege('imaging_ai_user', c.oid, 'SELECT'),
           'insert', has_table_privilege('imaging_ai_user', c.oid, 'INSERT'),
           'update', has_table_privilege('imaging_ai_user', c.oid, 'UPDATE'),
           'delete', has_table_privilege('imaging_ai_user', c.oid, 'DELETE'),
           'truncate', has_table_privilege('imaging_ai_user', c.oid, 'TRUNCATE'),
           'references', has_table_privilege('imaging_ai_user', c.oid, 'REFERENCES'),
           'trigger', has_table_privilege('imaging_ai_user', c.oid, 'TRIGGER'))
       END AS effective_acl
FROM pg_class c JOIN pg_namespace n ON c.relnamespace = n.oid
WHERE n.nspname IN ('clinical','system') AND c.relkind IN ('r','p','S')
ORDER BY 1,2;

SELECT a.attname, format_type(a.atttypid,a.atttypmod) AS data_type, NOT a.attnotnull AS nullable,
       has_column_privilege('imaging_ai_user', a.attrelid,a.attnum,'SELECT') AS can_select,
       has_column_privilege('imaging_ai_user', a.attrelid,a.attnum,'INSERT') AS can_insert,
       has_column_privilege('imaging_ai_user', a.attrelid,a.attnum,'UPDATE') AS can_update
FROM pg_attribute a WHERE a.attrelid = 'system.user_account'::regclass
  AND a.attnum > 0 AND NOT a.attisdropped ORDER BY a.attnum;

SELECT conname, convalidated, pg_get_constraintdef(oid) AS definition
FROM pg_constraint WHERE conrelid = 'system.user_account'::regclass AND conname LIKE 'ck_user_account_email%';
SELECT pg_get_indexdef(indexrelid) AS definition, indisvalid, indisready
FROM pg_index WHERE indexrelid = 'system.ux_user_account_email_normalized'::regclass;

SELECT current_database() AS database_name, pg_get_userbyid(datdba) AS owner,
       has_database_privilege('imaging_ai_user', oid, 'CONNECT') AS can_connect
FROM pg_database WHERE datname = current_database();
SELECT nspname, pg_get_userbyid(nspowner) AS owner,
       has_schema_privilege('imaging_ai_user', oid, 'USAGE') AS can_use,
       has_schema_privilege('imaging_ai_user', oid, 'CREATE') AS can_create
FROM pg_namespace WHERE nspname IN ('clinical','system','public','imaging_ai') ORDER BY 1;
ROLLBACK;
