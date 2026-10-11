-- Seats chosen on a seated show (ADR-027): kept on the order, then copied onto each ticket.
CREATE TABLE order_seats (
    order_id UUID NOT NULL REFERENCES orders(id) ON DELETE CASCADE,
    seat_id UUID NOT NULL,
    category_id UUID NOT NULL,
    section TEXT NOT NULL,
    row_label TEXT NOT NULL,
    seat_number INT NOT NULL CHECK (seat_number > 0),
    PRIMARY KEY (order_id, seat_id)
);

ALTER TABLE tickets ADD COLUMN seat_id UUID NULL;
ALTER TABLE tickets ADD COLUMN seat_section TEXT NULL;
ALTER TABLE tickets ADD COLUMN seat_row TEXT NULL;
ALTER TABLE tickets ADD COLUMN seat_number INT NULL;

-- A seat is on at most one ticket per order, so a repeated issue cannot double-book it.
CREATE UNIQUE INDEX ux_tickets_order_seat ON tickets (order_id, seat_id) WHERE seat_id IS NOT NULL;
