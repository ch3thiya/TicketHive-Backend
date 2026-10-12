CREATE TABLE refunds (
    order_id uuid PRIMARY KEY,
    amount numeric(12,2) NOT NULL CHECK(amount>=0),
    currency char(3) NOT NULL,
    status text NOT NULL CHECK(status IN ('Simulated','Pending','Succeeded','Failed')),
    reason text NOT NULL,
    created_at timestamptz NOT NULL
);
