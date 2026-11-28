-- Before the update commits: run ROLLBACK on the update connection instead.
-- After commit: use a site-specific script from the saved pre-update snapshot.
-- Compare owners, table/column/sequence ACL including grantors and grant options,
-- and effective privileges from PUBLIC and inherited roles. Restore only the
-- intentional differences, in one transaction; verify against that snapshot
-- before COMMIT. Obtain approval for any privilege increase.
-- Treat the Email columns as data. Check binding/verification counts and
-- inspect pg_depend, indexes, constraints and dependent application versions.
-- If bindings or confirmations exist, retain both columns and their data.
-- Even if empty, drop only objects proved to have been introduced by this update,
-- only after separate approval and dependency review; never use CASCADE.
BEGIN READ ONLY;
DO $phase7$
BEGIN
    RAISE EXCEPTION 'Automatic rollback refused: a verified site-specific pre-update owner and ACL snapshot is required';
END
$phase7$;
ROLLBACK;
