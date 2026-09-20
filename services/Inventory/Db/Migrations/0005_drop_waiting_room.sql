-- Inventory no longer owns a waiting room (ADR-002, ADR-020): HoldService
-- verifies admission tokens locally against the public key instead of
-- looking rows up here. Dropping the table takes ix_waiting_room_show_status_pos
-- and ix_waiting_room_token down with it, since both are owned by it.
-- Forward-only: 0003_add_waiting_room.sql stays as merged history.
DROP TABLE IF EXISTS waiting_room_entries;
