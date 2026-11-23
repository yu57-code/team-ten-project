-- Run manually as an authorized owner in imaging_ai_db only after review.
-- Append-only review and audit. No schema changes, no existing data changes.
-- No RETURNING is used; no SELECT on identity keys is required.
GRANT SELECT (report_id, review_status, review_comment, data_version, reviewer_id)
    ON system.review_record TO imaging_ai_user;
GRANT INSERT (report_id, reviewer_id, review_status, reviewed_at, review_comment, data_version)
    ON system.review_record TO imaging_ai_user;
GRANT INSERT (report_id, actor_id, action, after_json, data_version)
    ON system.audit_trail TO imaging_ai_user;
GRANT UPDATE (status, assigned_user_id)
    ON system.exception_queue TO imaging_ai_user;

-- Already granted and reused: clinical_report SELECT(report_id, processing_status,
-- storage_uri, sha256_hash, is_deleted), plus an existing UPDATE column for FOR UPDATE;
-- exception_queue SELECT(exception_id, report_id, exception_category, status);
-- user_account SELECT(user_id,is_active); ocr_candidate_batch SELECT(report_id,
-- ocr_attempt_id,payload_sha256); processing_log INSERT(report_id,status).
-- All identity columns use their existing database defaults. No explicit nextval().
