-- Queues: one fairness queue per high-demand show, tracking the pre-queue
-- window, the on-sale time, and how far admission has advanced.
create table queues (
    show_id                 uuid primary key,
    on_sale_at              timestamptz not null,
    prequeue_opens_at       timestamptz not null,
    serving_number          bigint not null default 0,
    next_number             bigint not null default 0,
    admit_batch             int not null,
    admit_interval_seconds  int not null,
    status                  text not null,
    constraint queues_status_valid check (status in ('PreQueue', 'Open', 'Closed')),
    constraint queues_serving_number_non_negative check (serving_number >= 0),
    constraint queues_next_number_non_negative check (next_number >= 0),
    constraint queues_admit_batch_positive check (admit_batch > 0),
    constraint queues_admit_interval_positive check (admit_interval_seconds > 0)
);

-- Queue entries: one row per customer per show. The composite primary key is
-- what makes joining twice impossible; a second join is an upsert that
-- returns the existing row unchanged, never a new insert.
create table queue_entries (
    show_id         uuid not null references queues (show_id),
    customer_sub    text not null,
    random_rank     double precision not null,
    queue_number    bigint null,
    joined_at       timestamptz not null,
    admitted_at     timestamptz null,
    primary key (show_id, customer_sub)
);

create index queue_entries_show_id_queue_number_idx
    on queue_entries (show_id, queue_number);
