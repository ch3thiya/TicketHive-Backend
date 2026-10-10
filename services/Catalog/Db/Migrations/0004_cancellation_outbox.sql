CREATE TABLE show_cancellations (
    show_id uuid PRIMARY KEY,
    event_id uuid NOT NULL,
    organizer_id uuid NOT NULL,
    created_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    dispatched boolean NOT NULL DEFAULT false,
    sales_stopped boolean NOT NULL DEFAULT false,
    attempts int NOT NULL DEFAULT 0,
    retry_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    lease_until timestamptz,
    lease_token uuid
);

CREATE FUNCTION publish_show_cancellation() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF OLD.status='Cancelled' AND NEW.status<>'Cancelled' THEN
        RAISE EXCEPTION 'Cancelled shows are terminal' USING ERRCODE='23514';
    END IF;
    IF NEW.status='Cancelled' THEN
        INSERT INTO show_cancellations(show_id,event_id,organizer_id)
          SELECT NEW.id,NEW.event_id,organizer_id FROM events WHERE id=NEW.event_id
          ON CONFLICT DO NOTHING;
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER publish_show_cancellation AFTER UPDATE OF status ON shows
FOR EACH ROW EXECUTE FUNCTION publish_show_cancellation();

CREATE FUNCTION cascade_event_cancellation() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF OLD.status='Cancelled' AND NEW.status<>'Cancelled' THEN
        RAISE EXCEPTION 'Cancelled events are terminal' USING ERRCODE='23514';
    END IF;
    IF NEW.status='Cancelled' THEN UPDATE shows SET status='Cancelled' WHERE event_id=NEW.id; END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER cascade_event_cancellation AFTER UPDATE OF status ON events
FOR EACH ROW EXECUTE FUNCTION cascade_event_cancellation();

-- Catch a show being added concurrently with event cancellation.
CREATE FUNCTION guard_show_parent() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    PERFORM 1 FROM events WHERE id=NEW.event_id AND status<>'Cancelled' FOR SHARE;
    IF NOT FOUND THEN RAISE EXCEPTION 'Event is cancelled' USING ERRCODE='23514'; END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER guard_show_parent BEFORE INSERT ON shows FOR EACH ROW EXECUTE FUNCTION guard_show_parent();
