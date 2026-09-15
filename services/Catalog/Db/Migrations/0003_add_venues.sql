-- Venues are reference data owned by Catalog (ADR-001). Ids are generated in
-- code (Guid.CreateVersion7()) and timestamps are set from the injected
-- TimeProvider (ADR-004), so this table has no server-side defaults for
-- id/created_at/updated_at.
CREATE TABLE IF NOT EXISTS venues (
    id UUID PRIMARY KEY,
    name TEXT NOT NULL,
    address TEXT NOT NULL,
    capacity INT NOT NULL CHECK (capacity > 0),
    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL
);

-- The venue list is always read ordered by name.
CREATE INDEX IF NOT EXISTS idx_venues_name ON venues(name);

-- shows.venue_id has accepted any GUID with no table behind it until now, so
-- existing rows may reference an id that was never a real venue. Postgres
-- refuses to add a foreign key that existing rows would violate, so those
-- values are cleared to NULL (still allowed on shows) before the constraint
-- below is added. This must run after CREATE TABLE venues above and before
-- the ALTER TABLE below.
UPDATE shows
SET venue_id = NULL
WHERE venue_id IS NOT NULL
  AND venue_id NOT IN (SELECT id FROM venues);

ALTER TABLE shows
    ADD CONSTRAINT fk_shows_venue_id FOREIGN KEY (venue_id) REFERENCES venues(id);

CREATE INDEX IF NOT EXISTS idx_shows_venue_id ON shows(venue_id);
