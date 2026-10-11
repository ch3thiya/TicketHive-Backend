-- One reminder per order (SCRUM-172). The order id is the primary key, so a reminder can never be
-- recorded, claimed or sent twice for the same order, however many instances or runs there are.
CREATE TABLE event_reminders (
    order_id uuid PRIMARY KEY,
    show_id uuid NOT NULL,
    recipient_email varchar(255) NOT NULL,
    customer_name varchar(255) NOT NULL,
    ticket_count int NOT NULL CHECK (ticket_count > 0),
    status text NOT NULL CHECK (status IN
        ('Pending', 'Sending', 'Sent', 'Simulated', 'Failed', 'NeedsReconciliation', 'Skipped', 'MissingRecipient')),
    skip_reason text NULL,
    attempts int NOT NULL DEFAULT 0 CHECK (attempts >= 0),
    next_attempt_at timestamptz NOT NULL,
    last_error text NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    sent_at timestamptz NULL
);

-- The job only ever looks for pending reminders that are due.
CREATE INDEX ix_event_reminders_due ON event_reminders (next_attempt_at) WHERE status = 'Pending';
CREATE INDEX ix_event_reminders_sending ON event_reminders (updated_at) WHERE status = 'Sending';
