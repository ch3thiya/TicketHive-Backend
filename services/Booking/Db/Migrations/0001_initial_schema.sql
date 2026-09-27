CREATE TABLE IF NOT EXISTS orders (
    id UUID PRIMARY KEY,
    hold_id UUID NOT NULL,
    customer_sub TEXT NOT NULL,
    customer_email TEXT NULL,
    customer_name TEXT NULL,
    show_id UUID NOT NULL,
    status TEXT NOT NULL,
    total_amount NUMERIC(12, 2) NOT NULL,
    currency CHAR(3) NOT NULL DEFAULT 'LKR',
    idempotency_key TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL,
    UNIQUE (customer_sub, idempotency_key)
);

ALTER TABLE orders ADD COLUMN IF NOT EXISTS customer_email TEXT NULL;
ALTER TABLE orders ADD COLUMN IF NOT EXISTS customer_name TEXT NULL;

CREATE INDEX IF NOT EXISTS ix_orders_hold_id ON orders (hold_id);
CREATE INDEX IF NOT EXISTS ix_orders_customer_sub ON orders (customer_sub);

CREATE TABLE IF NOT EXISTS order_items (
    order_id UUID NOT NULL REFERENCES orders(id) ON DELETE CASCADE,
    category_id UUID NOT NULL,
    quantity INT NOT NULL CHECK (quantity > 0),
    unit_price NUMERIC(12, 2) NOT NULL,
    PRIMARY KEY (order_id, category_id)
);

CREATE TABLE IF NOT EXISTS tickets (
    id UUID PRIMARY KEY,
    order_id UUID NOT NULL REFERENCES orders(id),
    category_id UUID NOT NULL,
    show_id UUID NOT NULL,
    customer_sub TEXT NOT NULL,
    unique_code TEXT NOT NULL UNIQUE,
    price NUMERIC(12, 2) NOT NULL,
    issued_at TIMESTAMPTZ NOT NULL,
    used_at TIMESTAMPTZ NULL,
    used_by TEXT NULL
);

CREATE INDEX IF NOT EXISTS ix_tickets_customer_sub ON tickets (customer_sub);
CREATE INDEX IF NOT EXISTS ix_tickets_unique_code ON tickets (unique_code);
