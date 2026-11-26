-- Execute as one Npgsql command inside a client transaction, using four text parameters.
-- If a username already exists (including concurrently), UNIQUE aborts the whole INSERT.
WITH role_ids AS (
    SELECT
        (SELECT role_id FROM system.role WHERE role_code = 'MEDICAL_REVIEWER') AS reviewer,
        (SELECT role_id FROM system.role WHERE role_code = 'SYSTEM_ADMIN') AS admin
), desired(username, password_hash, primary_role, secondary_role, is_active) AS (
    VALUES ('413570452', @hash_413570452, 'reviewer', 'admin', true),
           ('413570438', @hash_413570438, 'admin', NULL, true),
           ('413570359', @hash_413570359, 'reviewer', NULL, true),
           ('413570309', @hash_413570309, 'reviewer', NULL, false)
)
INSERT INTO system.user_account (username, password_hash, role_id, secondary_role_id, is_active)
SELECT username, password_hash,
       CASE primary_role WHEN 'reviewer' THEN reviewer WHEN 'admin' THEN admin END,
       CASE secondary_role WHEN 'admin' THEN admin END, is_active
FROM desired CROSS JOIN role_ids
WHERE current_database() = 'imaging_ai_db' AND reviewer IS NOT NULL AND admin IS NOT NULL;
