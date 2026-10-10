# Cancellation implementation and review guide

References: SCRUM-169 / US-10 and US-15. Implementation branch: `feature/order-cancellation`.

## Proposed PR description

Paid orders previously had no cancellation/refund workflow, and payment callbacks
could overwrite terminal order states. This change adds customer cancellation,
automatic show/event cancellation, voided tickets, recoverable inventory returns,
idempotent simulated refunds, deduplicated cancellation notification work, and
progress APIs used by the frontend.

Booking locks the order for cancellation and ticket validation; ticket issuance
locks that same order and refuses cancelled orders. Cancellation, voiding and work
creation commit together. Unique order refund keys and hold state transitions make
remote retries safe. Catalog records show-cancellation events in a transactional
outbox, including event cascading. Its dispatcher delivers events using authenticated
idempotent HTTP PUTs, independently of Kafka availability. Leases expire after two
minutes; failed work retries after fifteen seconds; workers poll every five seconds.

Cancellation requests install both Inventory and Booking show tombstones before returning an accepted response.
If that call fails, the durable cancellation remains recorded and recovery continues;
the request does not report successful completion. Existing pending orders are
cancelled by the dispatcher, and new orders are fenced by Booking show tombstones.

Refund completion is separate from order cancellation. A late success for an unpaid
cancelled order schedules its refund without reconfirming it. Sandbox confirmation
requires authentication, ownership, and explicit `PayHere:EnableSandboxConfirmation`.

## API contract

All public endpoints require user authentication. Internal endpoints additionally
require `aut=APPLICATION` and the exact scope below, including in Development.
Controllers are included in each service's generated `/openapi/v1.json` document.

| Endpoint | Result / scope |
|---|---|
| `POST /api/booking/orders/{id}/cancel` | 202 accepted, 200 duplicate, 404 non-owner/missing, 409 ineligible, 503 Catalog unavailable |
| `GET /api/booking/orders/{id}/cancellation` | Owner-only refund, inventory and notification status |
| `POST /api/catalog/shows/{id}/cancel` | Active owning organizer; 202 after sales stop |
| `POST /api/catalog/events/{id}/cancel` | Active owning organizer; cascades all shows |
| `GET /api/catalog/shows/{id}/cancellation` | Owning organizer; sales stop and order progress |
| `POST /api/catalog/admin/shows/{id}/cancel` | Admin override, including suspended organizer |
| `POST /api/catalog/admin/events/{id}/cancel` | Admin event cascade |
| `GET /internal/catalog/cancellations/shows/{id}/rules` | `catalog:read`; show start instant |
| `PUT /internal/inventory/cancellations/shows/{id}` | `inventory:write`; close sales/release holds |
| `PUT /internal/inventory/cancellations/holds/{id}` | `inventory:write`; return held/sold stock once |
| `PUT /internal/booking/cancellations/shows/{id}` | `booking:write`; cancel affected orders |
| `GET /internal/booking/cancellations/shows/{id}/progress` | `booking:write`; counts |
| `PUT /internal/payment/refunds/{orderId}` | `payment:refund`; `{amount,currency}`; mismatched replay is 409 |
| `PUT /internal/notification/cancellations` | `notification:write`; durable deduplicated message |
| `GET /internal/notification/cancellations/status?key=...` | `notification:write`; terminal delivery/reconciliation status; never resends |

The order ID is the canonical cancellation/refund operation key; a changed client
`Idempotency-Key` never creates a second operation. Notification keys are `order:{id}`
for customer cancellation and `show:{showId}:{customerSub}` for show cancellation.
No internal route should be exposed through Gateway.

## Configuration and rollout prerequisites

Run all five new service migrations before starting the new code. Do not enable a
mixed old/new Booking deployment: old validation code does not acquire the new order
lock. Drain/replace old Booking instances before exposing cancellation.

Configure `CatalogService:BaseUrl`, `InventoryService:BaseUrl`,
`PaymentService:BaseUrl`, and `NotificationService:BaseUrl` in Booking, and
`InventoryService:BaseUrl` and `BookingService:BaseUrl` in Catalog. Base URLs must
end in `/`. Local ports are Catalog 5142, Inventory 5219, Booking 5005,
Payment 5006, Notification 5007.

The existing `Wso2:InternalApi` client-credentials settings are reused. Booking needs
scopes `catalog:read inventory:write payment:refund notification:write`; Catalog
needs `inventory:write booking:write` in addition to its existing Identity scope.
Provision these grants in Asgardeo. Configure Notification's `Jwt:Authority` to the
same trusted issuer used by the other services. No credentials are added here.

Refunds require a successful Payment record and an exact match with its verified
amount and currency. They always record `Simulated`: the DevOps sandbox refund endpoint/configuration
has not been supplied. No refund HTTP call is made and no money moves. Do not
describe `Simulated` as an actual provider refund.

Cancellation email defaults to `CancellationEmail:Simulate=true`, recording
`Simulated` without sending. For an approved live email integration, configure
`Simulate=false`, `ServiceId`, `TemplateId`, `PublicKey`, `PrivateKey` under
`CancellationEmail`. The separate EmailJS template must render `subject`, `message`,
`show_id`, `order_ids`, `total_amount`, `customer_name`, and `to_email`.

EmailJS has no duplicate-send protection in the available adapter. `Sending` records
older than five minutes and ambiguous provider outcomes become `NeedsReconciliation`.
Do not blindly requeue these: inspect provider delivery evidence first. Missing
recipient data is recorded as `MissingRecipient`. Exactly-once external delivery
cannot be claimed with this provider. Database enqueue/claim deduplication is tested.

An unpaid cancellation can receive a late successful payment after its summary
email has already been sent. The refund is still processed automatically; the
same email key is not resent. Support must reconcile communication in this unusual
case. Guaranteed email delivery after arbitrarily late callbacks requires a
provider settlement boundary that is not present in the existing payment flow.

## Acceptance and outstanding decisions

| Story / criterion | Status | Review result |
|---|---|---|
| Customer AC1 | Complete | Owner-only future-show cancellation transactionally cancels the order and voids unused tickets. |
| Customer AC2 | Simulated | Full verified paid amount is recorded as a sandbox refund; no money moves. |
| Customer AC3 | Blocked | GA stock is returned idempotently. Seat return awaits a seating model. |
| Customer AC4 | Complete | Voided tickets are displayed as cancelled and fail locked entry validation. |
| Customer AC5 | Complete | Non-owner, used-ticket, and started-show attempts do not mutate state. |
| Customer AC6 | Complete | Durable unique work, stock return, and refund operation keys make retries safe. |
| Customer AC7 | Simulated | Delivery work runs after effects, but default email delivery is simulated. |
| Show AC1 | Complete | Acknowledgement waits for Inventory and Booking tombstones; post-ack holds and orders are rejected. |
| Show AC2 | Complete | Unpaid orders and their reservations are cancelled without refund. |
| Show AC3 | Simulated | Paid orders are cancelled and voided; verified full refunds are simulated. |
| Show AC4 | Simulated | One customer/show notification record is produced; default delivery is simulated. |
| Show AC5 | Complete | Event cancellation transactionally cascades to every show. |
| Show AC6 | Complete | Automated retries do not duplicate refund or notification records. Ambiguous email delivery becomes terminal reconciliation work and is never blindly resent. |
| Show AC7 | Complete | Organizer progress distinguishes refunded, simulated, processing, pending notification, and reconciliation counts. |

Seated shows are not implemented in this repository: no seat schema or identifiers
exist. The seated return part of customer AC3 remains blocked by that dependency.
The cancellation-until-show-start window and per-customer/per-show summary email
interpretation await Product Owner confirmation. Dates/times are provisionally
interpreted in Asia/Colombo because Catalog has no per-venue timezone.

## Sprint Review demonstration (isolated sandbox)

1. Use only local databases and fake/simulated provider integrations. Create a future
   GA show with two categories and a paid order with multiple unused tickets.
2. Cancel from My Tickets, confirm the entire-order warning, and observe cancelled
   cards, no usable QR, inventory restoration and explicit simulated refund status.
3. Repeat cancellation; compare the single Booking work row, single Payment refund
   row and inventory counters. Validate a voided code and observe HTTP 409.
4. Demonstrate non-owner, used-ticket and started-show refusals without mutation.
5. Create paid, used-ticket and unpaid orders across multiple customers/shows; cancel
   an event and watch each show's progress. All shows stop selling, held tickets are
   released, paid orders receive simulated refunds, and customer/show emails group
   multiple orders into one durable message.
6. Stop a downstream test service, cancel, restart it and observe automatic recovery.
   Duplicate remote requests may occur after timeouts; logical stock/refund effects
   and message records remain unique. No system can guarantee one network request
   across an ambiguous response while also guaranteeing retries.

## Verification

SQL integration tests use disposable PostgreSQL containers; provider tests use
in-memory HTTP handlers. Run `dotnet test TicketHive.slnx --configuration Release`,
`dotnet format TicketHive.slnx --verify-no-changes --no-restore`, and the frontend's
build, lint and `npm test -- --maxWorkers=2`. Hosted provider services must not be
started against actual payment/email credentials during verification.
