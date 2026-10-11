-- Seat-level holds (ADR-027). A seat belongs to at most one hold; the hold's status drives the seat.
ALTER TABLE show_seats ADD COLUMN hold_id uuid NULL REFERENCES holds (id);
ALTER TABLE show_seats ADD COLUMN ordinal int NOT NULL DEFAULT 0;

-- A seat is Available exactly when no hold owns it.
ALTER TABLE show_seats ADD CONSTRAINT ck_show_seats_hold_matches_status
    CHECK ((status = 'Available') = (hold_id IS NULL));

CREATE INDEX ix_show_seats_hold ON show_seats (hold_id) WHERE hold_id IS NOT NULL;

-- Every way a hold ends (expiry sweep, customer cancel, release on payment failure, order
-- cancellation) is a status change on the hold, so the seat follows it here, in the same
-- transaction, instead of in each caller. Repeating a transition changes nothing: the trigger only
-- fires when the status actually changes, and the updates below match nothing the second time.
CREATE FUNCTION sync_seats_with_hold() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.status IN ('Cancelled', 'Expired') THEN
        UPDATE show_seats SET status = 'Available', hold_id = NULL WHERE hold_id = NEW.id;
    ELSIF NEW.status = 'Converted' THEN
        UPDATE show_seats SET status = 'Sold' WHERE hold_id = NEW.id;
    END IF;
    RETURN NEW;
END $$;

CREATE TRIGGER trg_holds_sync_seats
    AFTER UPDATE OF status ON holds
    FOR EACH ROW
    WHEN (OLD.status IS DISTINCT FROM NEW.status)
    EXECUTE FUNCTION sync_seats_with_hold();
