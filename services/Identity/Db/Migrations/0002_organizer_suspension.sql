-- Durable, append-only history of organizer suspensions and reinstatements.
-- organizer_id deliberately has no foreign key: the audit trail must outlive the
-- account row (account sync can purge an account deleted in Asgardeo).
CREATE TABLE organizer_status_audit (
    id uuid PRIMARY KEY,
    organizer_id uuid NOT NULL,
    action varchar(20) NOT NULL CHECK (action IN ('Suspended', 'Reinstated')),
    reason text NOT NULL CHECK (length(btrim(reason)) > 0),
    actor_sub varchar(255) NOT NULL CHECK (length(btrim(actor_sub)) > 0),
    occurred_at timestamptz NOT NULL
);

CREATE INDEX ix_organizer_status_audit_organizer_time
    ON organizer_status_audit (organizer_id, occurred_at DESC);

CREATE FUNCTION organizer_status_audit_immutable() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'organizer_status_audit rows are append-only';
END $$;

CREATE TRIGGER trg_organizer_status_audit_immutable
    BEFORE UPDATE OR DELETE ON organizer_status_audit
    FOR EACH ROW EXECUTE FUNCTION organizer_status_audit_immutable();
