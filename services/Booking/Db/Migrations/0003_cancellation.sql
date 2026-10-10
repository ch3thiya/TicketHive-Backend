ALTER TABLE tickets ADD COLUMN voided_at timestamptz;

CREATE TABLE cancelled_shows (
    show_id uuid PRIMARY KEY,
    cancelled_at timestamptz NOT NULL
);
CREATE TABLE order_cancellations (
    order_id uuid PRIMARY KEY REFERENCES orders(id),
    reason text NOT NULL CHECK (reason IN ('Customer','Show')),
    refund_status text NOT NULL CHECK (refund_status IN ('Pending','NotRequired','Simulated','Succeeded')),
    inventory_returned boolean NOT NULL DEFAULT false,
    notification_status text NOT NULL DEFAULT 'Pending',
    created_at timestamptz NOT NULL,
    attempts int NOT NULL DEFAULT 0,
    retry_at timestamptz NOT NULL,
    lease_until timestamptz,
    lease_token uuid,
    last_error text
);
CREATE INDEX ix_cancellation_work ON order_cancellations(retry_at);

-- Payment success arriving after unpaid show cancellation must be refunded,
-- without changing the terminal order state. Serialize with cancellation.
CREATE FUNCTION apply_order_status(p_id uuid,p_status text,p_now timestamptz)
RETURNS boolean LANGUAGE plpgsql AS $$
DECLARE current_status text;
BEGIN
    SELECT status INTO current_status FROM orders WHERE id=p_id FOR UPDATE;
    IF current_status='Cancelled' AND p_status='Confirmed' THEN
        UPDATE order_cancellations SET refund_status='Pending',retry_at=p_now
          WHERE order_id=p_id AND refund_status='NotRequired';
        RETURN false;
    END IF;
    IF current_status IS DISTINCT FROM 'PaymentPending' THEN RETURN false; END IF;
    UPDATE orders SET status=p_status,updated_at=p_now WHERE id=p_id;
    RETURN true;
END $$;

-- Both cancellation and entry validation lock the order before touching tickets.
CREATE FUNCTION validate_ticket(p_code text, p_actor text, p_now timestamptz)
RETURNS boolean LANGUAGE plpgsql AS $$
DECLARE v_order uuid;
BEGIN
    SELECT order_id INTO v_order FROM tickets WHERE unique_code=p_code;
    PERFORM 1 FROM orders WHERE id=v_order AND status='Confirmed' FOR UPDATE;
    IF NOT FOUND THEN RETURN false; END IF;
    UPDATE tickets SET used_at=p_now,used_by=p_actor
      WHERE unique_code=p_code AND used_at IS NULL AND voided_at IS NULL;
    RETURN FOUND;
END $$;

-- Issuance cannot insert live tickets after a concurrent cancellation.
CREATE FUNCTION guard_ticket_issuance() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    PERFORM 1 FROM orders WHERE id=NEW.order_id AND status='Confirmed' FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'Order is not confirmed' USING ERRCODE='23514'; END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER guard_ticket_issuance BEFORE INSERT ON tickets
FOR EACH ROW EXECUTE FUNCTION guard_ticket_issuance();

CREATE FUNCTION guard_cancelled_order() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP='UPDATE' AND OLD.status='Cancelled' AND NEW.status<>'Cancelled' THEN
        RAISE EXCEPTION 'Cancelled orders are terminal' USING ERRCODE='23514';
    END IF;
    IF TG_OP='INSERT' THEN
        PERFORM pg_advisory_xact_lock_shared(hashtextextended(NEW.show_id::text, 169));
        IF EXISTS(SELECT 1 FROM cancelled_shows WHERE show_id=NEW.show_id) THEN
            RAISE EXCEPTION 'Show is cancelled' USING ERRCODE='23514';
        END IF;
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER guard_cancelled_order BEFORE INSERT OR UPDATE ON orders
FOR EACH ROW EXECUTE FUNCTION guard_cancelled_order();
