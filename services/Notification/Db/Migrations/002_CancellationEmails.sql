CREATE TABLE cancellation_emails (
    operation_key text PRIMARY KEY,
    payload jsonb NOT NULL,
    status text NOT NULL CHECK(status IN ('Pending','Sending','Sent','Simulated','NeedsReconciliation','MissingRecipient')),
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL
);
