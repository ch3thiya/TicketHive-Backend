-- When the serving number last advanced. The advance only fires once
-- admit_interval_seconds have passed since this time, so the admission
-- rate holds no matter how many scheduler instances run. Null means the
-- queue has never advanced.
alter table queues add column last_advanced_at timestamptz null;
