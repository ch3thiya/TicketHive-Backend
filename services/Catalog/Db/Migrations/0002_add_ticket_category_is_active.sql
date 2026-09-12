-- Ticket categories are never deleted and re-inserted (ADR-004); retiring one
-- sets is_active = false instead so referencing rows (stock, holds, tickets)
-- keep pointing at a real id.
ALTER TABLE ticket_categories ADD COLUMN IF NOT EXISTS is_active BOOLEAN NOT NULL DEFAULT true;
