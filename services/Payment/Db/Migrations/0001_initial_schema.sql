CREATE TABLE IF NOT EXISTS payments (
    id UUID PRIMARY KEY,
    order_id UUID NOT NULL,
    customer_sub TEXT NOT NULL,
    amount NUMERIC(12, 2) NOT NULL,
    currency CHAR(3) NOT NULL DEFAULT 'LKR',
    status TEXT NOT NULL,
    payhere_payment_id TEXT NULL,
    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_payments_order_id ON payments (order_id);
CREATE INDEX IF NOT EXISTS ix_payments_customer_sub ON payments (customer_sub);
