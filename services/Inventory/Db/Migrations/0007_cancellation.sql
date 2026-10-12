CREATE TABLE cancelled_shows(show_id uuid PRIMARY KEY);

CREATE FUNCTION return_cancelled_hold(p_id uuid) RETURNS boolean LANGUAGE plpgsql AS $$
DECLARE h holds%ROWTYPE; item record;
BEGIN
    SELECT * INTO h FROM holds WHERE id=p_id FOR UPDATE;
    IF NOT FOUND THEN RETURN false; END IF;
    IF h.status IN ('Cancelled','Expired') THEN RETURN true; END IF;
    FOR item IN SELECT * FROM hold_items WHERE hold_id=p_id ORDER BY category_id LOOP
        UPDATE stock SET available=available+item.quantity,
          sold=sold-CASE WHEN h.status='Converted' THEN item.quantity ELSE 0 END
          WHERE show_id=h.show_id AND category_id=item.category_id;
    END LOOP;
    UPDATE customer_quotas SET quantity=GREATEST(0,quantity-(SELECT COALESCE(SUM(quantity),0) FROM hold_items WHERE hold_id=p_id))
      WHERE show_id=h.show_id AND customer_sub=h.customer_sub;
    UPDATE holds SET status='Cancelled' WHERE id=p_id;
    RETURN true;
END $$;

-- Admission and closure share a show lock. The repository acquires this before
-- quota/stock locks, preserving a consistent lock order.
CREATE FUNCTION close_cancelled_show(p_show uuid) RETURNS void LANGUAGE plpgsql AS $$
DECLARE h record;
BEGIN
    PERFORM pg_advisory_xact_lock(hashtextextended(p_show::text,169));
    INSERT INTO cancelled_shows VALUES(p_show) ON CONFLICT DO NOTHING;
    FOR h IN SELECT id FROM holds WHERE show_id=p_show AND status IN ('Active','PaymentPending') ORDER BY id LOOP
        PERFORM return_cancelled_hold(h.id);
    END LOOP;
END $$;
