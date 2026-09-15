-- customer_quotas, holds and hold_items support S2-03 ticket holds. Same
-- ADR-004 conventions as 0001: ids from Guid.CreateVersion7() in code,
-- timestamptz throughout, and rows change state in place rather than being
-- deleted and re-inserted.
CREATE TABLE IF NOT EXISTS customer_quotas (
    show_id UUID NOT NULL,
    customer_sub TEXT NOT NULL,
    quantity INT NOT NULL,
    PRIMARY KEY (show_id, customer_sub)
);

CREATE TABLE IF NOT EXISTS holds (
    id UUID PRIMARY KEY,
    show_id UUID NOT NULL,
    customer_sub TEXT NOT NULL,
    status TEXT NOT NULL,
    expires_at TIMESTAMPTZ NOT NULL,
    idempotency_key TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL,
    UNIQUE (customer_sub, idempotency_key)
);

-- The S2-04 expiry sweeper will claim Active holds past their expires_at;
-- index what it filters and orders by while this table is being created.
CREATE INDEX IF NOT EXISTS ix_holds_status_expires_at ON holds (status, expires_at);

CREATE TABLE IF NOT EXISTS hold_items (
    hold_id UUID NOT NULL,
    category_id UUID NOT NULL,
    quantity INT NOT NULL CHECK (quantity > 0),
    unit_price NUMERIC(12, 2) NOT NULL,
    PRIMARY KEY (hold_id, category_id)
);
