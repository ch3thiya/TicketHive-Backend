# ADR-022: Durable cancellation and sandbox refunds

- Status: Proposed for Product Owner and engineering review
- References: SCRUM-169 / US-10, US-15

Customer cancellation is permitted strictly before the show's start, provided no
ticket has been used. Product Owner confirmation of this window remains outstanding.
Catalog currently stores local dates/times without a venue timezone: interpret show
start in Asia/Colombo provisionally. Automatic show cancellation has no used-ticket
or start-time restriction.

Cancellation and refund are separate states. Booking commits cancellation, ticket
voiding and durable work together. A worker retries authenticated, idempotent internal
operations. Each service owns its database and operation keys. Validation and
cancellation serialize on the Booking order row. Cancelled orders are terminal.

Catalog cancellation is a durable event per show, including event-to-show cascade.
Inventory closes admission and returns reservations; Booking processes affected orders.
Progress distinguishes simulated refunds from actual provider refunds.

The DevOps PayHere refund route/configuration is absent. The implementation records
`Simulated` refunds explicitly; no money moves and no real payment endpoint is called.
Production refund support must not be enabled until provider idempotency and recovery
semantics have been established.

US-15 mixes order-level refund processing with one email per customer per show.
Provisional interpretation: wait for all that customer's affected show orders, then
send one summary keyed by show and customer. US-10 uses an order-level key. A customer
who cancelled before the show cancellation is excluded from its affected orders.
Product Owner confirmation of this interpretation remains outstanding.

The current EmailJS adapter exposes no idempotency key or delivery lookup. A database
unique key prevents ordinary duplicates but cannot prove exactly-once external email
delivery. Ambiguous sends require reconciliation rather than automatic resend. This
provider limitation prevents claiming both guaranteed delivery and exactly-once
delivery across arbitrary crashes. Verification uses fakes/simulation only.

Inventory is currently GA-only; no seat identifiers or seat allocation implementation
exist. Preserve GA accounting; seated-show acceptance requires the separate seating
dependency and must not be represented as implemented.
