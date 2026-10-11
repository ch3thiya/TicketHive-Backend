-- Seat map requests and versioned layouts (ADR-026). A show is either GA or seated.
ALTER TABLE shows ADD COLUMN IF NOT EXISTS seating_mode text NOT NULL DEFAULT 'GA'
    CHECK (seating_mode IN ('GA', 'Seated'));

CREATE TABLE seat_map_requests (
    id uuid PRIMARY KEY,
    show_id uuid NOT NULL REFERENCES shows (id),
    organizer_id uuid NOT NULL,
    description text NOT NULL CHECK (length(btrim(description)) > 0),
    status varchar(20) NOT NULL CHECK (status IN ('Pending', 'Completed', 'Rejected')),
    rejection_reason text NULL,
    requested_at timestamptz NOT NULL,
    decided_at timestamptz NULL,
    decided_by varchar(255) NULL,
    CHECK (status <> 'Rejected' OR length(btrim(coalesce(rejection_reason, ''))) > 0)
);

-- At most one open request per show.
CREATE UNIQUE INDEX ux_seat_map_requests_pending_per_show
    ON seat_map_requests (show_id) WHERE status = 'Pending';
CREATE INDEX ix_seat_map_requests_organizer ON seat_map_requests (organizer_id, requested_at DESC);

CREATE TABLE seat_layouts (
    id uuid PRIMARY KEY,
    show_id uuid NOT NULL REFERENCES shows (id),
    request_id uuid NOT NULL REFERENCES seat_map_requests (id),
    version int NOT NULL CHECK (version > 0),
    status varchar(20) NOT NULL CHECK (status IN ('Draft', 'Enabled', 'Superseded')),
    layout jsonb NOT NULL,
    seat_count int NOT NULL CHECK (seat_count > 0),
    created_by varchar(255) NOT NULL,
    created_at timestamptz NOT NULL,
    enabled_at timestamptz NULL,
    UNIQUE (show_id, version)
);

-- At most one enabled layout per show.
CREATE UNIQUE INDEX ux_seat_layouts_enabled_per_show
    ON seat_layouts (show_id) WHERE status = 'Enabled';
