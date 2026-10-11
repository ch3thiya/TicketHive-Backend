-- Seats for shows with an enabled seat layout (ADR-026). A show is either GA or seated: enabling
-- a layout flips the show's stock rows to allocation_mode 'Seated' and creates one row per seat.
CREATE TABLE show_seat_layouts (
    show_id uuid PRIMARY KEY,
    version int NOT NULL CHECK (version > 0),
    seat_count int NOT NULL CHECK (seat_count > 0),
    enabled_at timestamptz NOT NULL
);

CREATE TABLE show_seats (
    id uuid PRIMARY KEY,
    show_id uuid NOT NULL,
    category_id uuid NOT NULL,
    section text NOT NULL CHECK (length(btrim(section)) > 0),
    row_label text NOT NULL CHECK (length(btrim(row_label)) > 0),
    seat_number int NOT NULL CHECK (seat_number > 0),
    status text NOT NULL DEFAULT 'Available' CHECK (status IN ('Available', 'Held', 'Sold')),
    FOREIGN KEY (show_id, category_id) REFERENCES stock (show_id, category_id),
    UNIQUE (show_id, section, row_label, seat_number)
);

CREATE INDEX ix_show_seats_show_category_status ON show_seats (show_id, category_id, status);
