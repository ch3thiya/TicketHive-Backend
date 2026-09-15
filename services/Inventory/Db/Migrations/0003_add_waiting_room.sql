-- waiting_room_entries supports S2-06 Waiting Room and Admission Tokens.
-- Tracks customer queue position and short-lived admission tokens for high-demand shows.
CREATE TABLE IF NOT EXISTS waiting_room_entries (
    id UUID PRIMARY KEY,
    show_id UUID NOT NULL,
    customer_sub TEXT NOT NULL,
    status TEXT NOT NULL,
    position INT NOT NULL,
    admission_token TEXT NULL,
    admitted_at TIMESTAMPTZ NULL,
    token_expires_at TIMESTAMPTZ NULL,
    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL,
    UNIQUE (show_id, customer_sub)
);

CREATE INDEX IF NOT EXISTS ix_waiting_room_show_status_pos ON waiting_room_entries (show_id, status, position);
CREATE INDEX IF NOT EXISTS ix_waiting_room_token ON waiting_room_entries (admission_token);
