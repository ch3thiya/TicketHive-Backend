# ADR-028: Event reminders are sent once per order from live data

- **Status:** Proposed for Product Owner and engineering review
- **Date:** 2026-10-11

## Context

A customer should get one email reminder shortly before an event. Notification already sends the ticket-issued and cancellation emails from Kafka events. The reminder needs the live show time (Catalog) and the live order state (Booking), and must never send twice, even with several Notification instances running.

## Decision

- **Notification owns the schedule.** Table `event_reminders` has one row per order (primary key `order_id`). It is created by the existing `tickets.issued` listener, before the confirmation email, from the event's `ShowId`, `TicketCount`, email and name. A redelivered event changes nothing.
- **Fixed 24-hour window (needs Product Owner confirmation).** A reminder is due when the show starts within `Reminders:WindowHours` (24) and has not started. Customers who buy inside the window are reminded on the next run before the show starts.
- **Live checks before every send, failing closed.** Catalog `POST /internal/catalog/shows/reminder-info` (scope `catalog:read`) returns the start in UTC, event, venue and whether the show or event is cancelled. Booking `POST /internal/booking/orders/reminder-eligibility` (scope `booking:read`) returns the orders that are still confirmed with their live, non-voided ticket count. Both accept 1–200 ids and require an Asgardeo application token with the exact scope. If either service cannot answer, nothing is sent and the reminders wait one minute.
- **At most once.** A pass claims due rows with `FOR UPDATE SKIP LOCKED`, so any number of instances share the work. A definite refusal by the email provider is retried after `RetryMinutes` (10) up to `MaxAttempts` (3), and only while the show has not started. A send with an unknown outcome (timeout, exception, or a claim stuck in `Sending` longer than `StaleSendMinutes`) becomes `NeedsReconciliation` and is never resent automatically.
- **Skipped, not sent:** cancelled or unknown show, started show, order no longer confirmed, and any missing, invalid or placeholder (`customer@tickethive.lk`) address. The reason is stored.
- **Content.** Event name, date and time in `Asia/Colombo`, venue (or "Venue to be announced") and ticket count.
- **Simulated by default.** `Reminders:Simulate` is `true`: rows end as `Simulated` and no provider is called. Orders, tickets and the existing emails are never touched by a reminder failure.
- **No backfill.** Only orders whose tickets are issued after this ships are recorded. Orders confirmed earlier get no reminder.
- **Idle until configured.** The worker does nothing until `Wso2:InternalApi` has a real client id and secret, so deploying before DevOps configuration is safe. `Reminders:Enabled=false` switches it off.
- **Telemetry.** Counters `notification.reminders.outcomes` (tag `outcome`) and `notification.reminders.runs`, histogram `notification.reminders.run_duration`. Logs never contain a full email address.

## Consequences

**Positive**

- One reminder per order, correct with any number of instances, and cancelled orders and shows are never reminded.
- No service reads another service's tables.

**Negative / trade-offs**

- A show moved earlier or later is picked up at the next look (at most `MaxDeferHours`, 6, before the window), not instantly.
- Ambiguous sends need a person to check the provider; a possible duplicate is judged worse than a missed reminder.
- Each pass makes one Catalog and one Booking call per batch.

## Handoff

**DevOps (nothing here has been configured):**

1. Add the scope `booking:read` to the shared Asgardeo machine-to-machine application (client id `HEfhmRO0hTg9mFtiSRrm_3MYfsAa`), alongside `catalog:read`.
2. Give the Notification service: `Wso2__InternalApi__TokenEndpoint`, `Wso2__InternalApi__ClientId` (secret ref `wso2-int-id`), `Wso2__InternalApi__ClientSecret` (secret ref `wso2-int-secret`), `Wso2__InternalApi__Scope="catalog:read booking:read"`, `CatalogService__BaseUrl`, `BookingService__BaseUrl`.
3. Apply migration `003_event_reminders.sql` (runs with Notification's DbUp at startup).
4. Live email later needs `Reminders__Simulate=false` plus `Reminders__ServiceId`, `Reminders__TemplateId`, `Reminders__PublicKey` and `Reminders__PrivateKey`. Keep `true` outside production.
5. Alert on `notification.reminders.outcomes` with `outcome` of `failed` or `needs_reconciliation`, and on `dependency_unavailable` that persists.

**Product Owner:** confirm the 24-hour window and the no-backfill limitation.

## Alternatives considered

- **Query Booking's database from Notification.** Rejected: breaks service data ownership (ADR-004, ADR-020).
- **Schedule from Booking.** Rejected: Notification already owns delivery and its retry state.
- **Retry ambiguous sends.** Rejected: risks duplicate emails.

## References

- ADR-005, ADR-012, ADR-022, ADR-024
