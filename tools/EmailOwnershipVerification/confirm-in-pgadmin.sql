-- Run in pgAdmin as the maintenance identity. This file is a template.
-- Fill every applicable field from the privately witnessed, live tool output.
-- No COMMIT is included. Review the notice and commit promptly in the same tab.
BEGIN;
SET LOCAL lock_timeout = '3s';
SET LOCAL statement_timeout = '10s';

DO $maintenance$
DECLARE
    p_target text := NULL;
    p_purpose text := NULL;
    p_evidence_id uuid := NULL;
    p_challenge_id uuid := NULL;
    p_user_id bigint := NULL;
    p_username text := NULL;
    p_email text := NULL;
    p_active boolean := NULL;
    p_primary_id bigint := NULL;
    p_secondary_id bigint := NULL;
    p_primary_code text := NULL;
    p_secondary_code text := NULL;
    p_xmin text := NULL;
    p_confirmed timestamptz := NULL;
    p_expiry timestamptz := NULL;
    affected integer;
BEGIN
    IF current_database() <> 'imaging_ai_db'
       OR inet_server_addr() IS DISTINCT FROM '127.0.0.1'::inet
       OR inet_server_port() IS DISTINCT FROM 5432
       OR current_user = 'imaging_ai_user'
       OR p_target IS DISTINCT FROM '127.0.0.1:5432/imaging_ai_db'
       OR p_purpose IS DISTINCT FROM 'email-ownership-confirmation-v1'
       OR p_evidence_id IS NULL OR p_challenge_id IS NULL
       OR p_user_id IS NULL OR p_username IS NULL
       OR p_username NOT IN ('413570452', '413570438', '413570359')
       OR p_email IS NULL OR p_active IS DISTINCT FROM true
       OR p_primary_id IS NULL OR p_primary_code IS NULL
       OR p_xmin IS NULL OR p_xmin !~ '^[0-9]{1,10}$'
       OR p_confirmed IS NULL OR p_expiry IS NULL
       OR p_confirmed > clock_timestamp()
       OR p_expiry <= p_confirmed OR p_expiry > p_confirmed + interval '5 minutes'
       OR clock_timestamp() >= p_expiry
    THEN
        RAISE EXCEPTION 'Wrong target or incomplete, ineligible or expired maintenance handoff';
    END IF;

    IF (CASE p_username
        WHEN '413570452' THEN p_secondary_id IS NOT NULL AND p_secondary_id <> p_primary_id AND
             ((p_primary_code = 'MEDICAL_REVIEWER' AND p_secondary_code = 'SYSTEM_ADMIN') OR
              (p_primary_code = 'SYSTEM_ADMIN' AND p_secondary_code = 'MEDICAL_REVIEWER'))
        WHEN '413570438' THEN p_primary_code = 'SYSTEM_ADMIN' AND p_secondary_id IS NULL AND p_secondary_code IS NULL
        WHEN '413570359' THEN p_primary_code = 'MEDICAL_REVIEWER' AND p_secondary_id IS NULL AND p_secondary_code IS NULL
        END) IS DISTINCT FROM true
    THEN
        RAISE EXCEPTION 'Unexpected role snapshot';
    END IF;

    UPDATE system.user_account a
       SET email_verified_at = p_confirmed
     WHERE a.user_id = p_user_id
       AND a.username = p_username
       AND a.email = p_email
       AND a.is_active = p_active
       AND a.email_verified_at IS NULL
       AND a.xmin::text = p_xmin
       AND a.role_id = p_primary_id
       AND a.secondary_role_id IS NOT DISTINCT FROM p_secondary_id
       AND EXISTS (SELECT 1 FROM system.role r WHERE r.role_id = a.role_id AND r.role_code = p_primary_code)
       AND (p_secondary_id IS NULL OR EXISTS (
            SELECT 1 FROM system.role r WHERE r.role_id = a.secondary_role_id AND r.role_code = p_secondary_code))
       AND clock_timestamp() < p_expiry;
    GET DIAGNOSTICS affected = ROW_COUNT;
    IF affected <> 1 OR clock_timestamp() >= p_expiry THEN
        RAISE EXCEPTION 'Expected exactly one unchanged account and unexpired handoff';
    END IF;
    RAISE NOTICE 'One account updated in this transaction. Confirm or roll back promptly.';
END
$maintenance$;

-- After reviewing: COMMIT; or ROLLBACK; (issue separately).
