-- gen_random_uuid() is built-in for PostgreSQL 13+ (no extension required)

-- Table for persisting events in catalog service
CREATE TABLE IF NOT EXISTS events (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    organizer_id UUID NOT NULL,
    name VARCHAR(255) NOT NULL,
    description TEXT NOT NULL DEFAULT '',
    category VARCHAR(100) NOT NULL DEFAULT '',
    event_date DATE,
    event_time TIME,
    banner_url TEXT NOT NULL DEFAULT '',
    status VARCHAR(50) NOT NULL DEFAULT 'Draft',
    cancellation_cutoff_hours INT,
    created_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP NOT NULL
);

-- Indexes for performance
CREATE INDEX IF NOT EXISTS idx_events_organizer_id ON events(organizer_id);
CREATE INDEX IF NOT EXISTS idx_events_status ON events(status);

-- Table for persisting shows in catalog service
CREATE TABLE IF NOT EXISTS shows (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    event_id UUID NOT NULL REFERENCES events(id) ON DELETE CASCADE,
    show_date DATE NOT NULL,
    show_time TIME NOT NULL,
    venue_id UUID,
    on_sale_at TIMESTAMP WITH TIME ZONE,
    high_demand_threshold INT,
    reminder_minutes_before INT,
    status VARCHAR(50) NOT NULL DEFAULT 'Active',
    created_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP NOT NULL
);

-- Table for persisting ticket categories in catalog service
CREATE TABLE IF NOT EXISTS ticket_categories (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    show_id UUID NOT NULL REFERENCES shows(id) ON DELETE CASCADE,
    name VARCHAR(100) NOT NULL,
    price NUMERIC(10, 2) NOT NULL,
    capacity INT NOT NULL,
    created_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP NOT NULL
);

-- Foreign key indexes
CREATE INDEX IF NOT EXISTS idx_shows_event_id ON shows(event_id);
CREATE INDEX IF NOT EXISTS idx_shows_status ON shows(status);
CREATE INDEX IF NOT EXISTS idx_ticket_categories_show_id ON ticket_categories(show_id);

-- Columns added after the original CREATE TABLE statements were written.
-- CREATE TABLE IF NOT EXISTS above is a no-op on a database that already has
-- these tables, so they are restated here as no-op ALTERs on a fresh database
-- and as the actual column additions on an existing one.
ALTER TABLE events ADD COLUMN IF NOT EXISTS cancellation_cutoff_hours INT;
ALTER TABLE shows ADD COLUMN IF NOT EXISTS venue_id UUID;
ALTER TABLE shows ADD COLUMN IF NOT EXISTS on_sale_at TIMESTAMP WITH TIME ZONE;
ALTER TABLE shows ADD COLUMN IF NOT EXISTS high_demand_threshold INT;
ALTER TABLE shows ADD COLUMN IF NOT EXISTS reminder_minutes_before INT;
