-- Reusable structural seat-map templates owned by venues. Ticket categories
-- remain show-specific and are assigned when an admin fulfils a seat-map request.
CREATE TABLE venue_seat_maps (
    venue_id uuid PRIMARY KEY REFERENCES venues (id) ON DELETE CASCADE,
    layout jsonb NOT NULL,
    seat_count int NOT NULL CHECK (seat_count > 0),
    created_by varchar(255) NOT NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL
);
