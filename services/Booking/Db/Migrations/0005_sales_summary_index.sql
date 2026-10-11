-- The organizer sales summary reads a show's confirmed orders on every request (SCRUM-171).
-- Booking had no index on show_id, so these keep that read cheap as orders grow.
CREATE INDEX IF NOT EXISTS ix_orders_show_status ON orders (show_id, status);
CREATE INDEX IF NOT EXISTS ix_tickets_show_used ON tickets (show_id) WHERE used_at IS NOT NULL AND voided_at IS NULL;
