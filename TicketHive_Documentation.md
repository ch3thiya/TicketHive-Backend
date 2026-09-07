# TicketHive - Software Requirements Specification (SRS)
**Project:** TicketHive  
**Description:** Multi Tenant Ticketing Platform for Live Events  
**Date:** Aug 16, 2026  

---

## 1. Introduction
TicketHive is a web-based multi-event ticket booking platform for events and movies. It allows users to discover events, join high-demand queues or waitlists, reserve tickets, make payments and receive ticket codes. Organizers create and manage events and promotions and view basic dashboard statistics. Platform administrators manage accounts and venues.

## 2. Problem
Organizers need more than a simple checkout page: they must manage events, venues, limited seat inventory, high-demand releases, waitlists, bookings, payments and customer notifications. Customers need reliable availability and protection against duplicate allocation during busy ticket releases.

## 3. Solution
TicketHive provides a single platform covering event publication through ticket delivery and validation. The system uses six independently deployable business services, with asynchronous event handling used to reduce direct service dependencies.

## 4. Objectives
* Provide a reliable multi-event ticketing platform for users and organizers.
* Prevent duplicate seat/ticket allocation.
* Support temporary holds and automatic release of expired inventory.
* Control access to high-demand releases through a waiting room.
* Allow customers to join a waitlist for sold-out shows.
* Support secure third-party payment and refund processing.
* Issue unique ticket IDs/codes without camera or QR scanning.
* Provide organizers with basic dynamic sales/booking statistics.
* Use event-driven communication where practical to reduce tight coupling.

## 5. Stakeholders
| Stakeholder | Interest / Responsibility |
| :--- | :--- |
| **Customers** | Discover events, join queues/waitlists, reserve tickets, pay, receive tickets and view bookings. |
| **Organizers** | Create/manage events, configure show details and promotions, monitor statistics and receive notifications. |
| **Administrators** | Manage accounts, approve organizer requests, approve venue requests and maintain platform venues. |
| **Technical Team** | Develop, test, deploy, monitor and maintain the platform. |
| **Third Party Providers** | Provide authentication, payment and email capabilities. |

## 6. User Types
| Stakeholder | Interest / Responsibility |
| :--- | :--- |
| **Customers** | Discover events, join queues/waitlists, reserve tickets, pay, receive tickets and view bookings. |
| **Organizers** | Create/manage events, configure show details and promotions, monitor statistics and receive notifications. |
| **Administrators** | Manage accounts, approve organizer requests, approve venue requests and maintain platform venues. |

## 7. Functional Requirements

### 7.1 Identity & Accounts
* The system shall allow users to authenticate through a third-party identity provider.
* The system shall restrict organizer functions until the request is approved.
* The system shall support User, Organizer and Platform Administrator roles.
* The system shall allow a user to submit an organizer approval request.
* The system shall allow an administrator to approve, reject or suspend organizer accounts.
* The system shall maintain application account records linked to the authenticated identity.

### 7.2 Catalog
* The system shall allow authorized organizers to create and manage their events.
* The system shall allow users to browse and search published events.
* The system shall allow an administrator to create and maintain venues and seating/capacity definitions.
* The system shall allow organizers to create shows with dates, times, ticket categories and prices.
* The system shall allow organizers to create, activate and deactivate promotion codes for their events.
* The system shall allow organizers to publish, edit and cancel permitted events/shows.
* The system shall display event details, venue, show times, prices and availability information.

### 7.3 Inventory
* The system shall display available, held and sold ticket/seat inventory.
* The system shall support reserved-seat and capacity-based ticket inventory.
* The system shall automatically release expired holds.
* The system shall enforce organizer-defined per-user ticket limits.
* The waiting room shall admit users in arrival order and issue an admission token.
* The system shall synchronously create temporary holds for selected available inventory and return the hold result to the requesting service immediately.
* The system shall prevent the same inventory unit from being held or sold to two users concurrently.
* The system shall place users in a waiting room when a show reaches its configured high-demand threshold.
* The system shall prevent users without valid admission authorization from creating purchase holds while the queue is active.

### 7.4 Booking
* The system shall create a booking for a valid inventory hold.
* The system shall calculate and store the booking total before payment.
* The system shall allow users to view booking history and status.
* The system shall support cancellation according to the event policy.
* The system shall return eligible cancelled inventory to the Inventory service.
* The system shall allow users to join a waitlist when inventory is sold out.
* The system shall maintain waitlist entries in arrival order.
* Expired waitlist offers shall be passed to the next eligible user.
* The system shall issue a unique ticket ID/code for each confirmed ticket.
* When inventory is released, the system shall create a time-limited offer for an eligible waitlisted user.
* The system shall allow an organizer to validate a ticket ID/code and mark it as used.

### 7.5 Payment
* The system shall use a third-party payment gateway in sandbox/test mode.
* The system shall record payment outcomes for bookings.
* The system shall not store payment card information.
* Successful payment shall lead to booking confirmation and ticket issuance.
* Failed or cancelled payment shall result in booking failure and inventory release.
* The system shall support eligible refunds through the third-party gateway.
* Payment callbacks/events shall be handled idempotently.

### 7.6 Notification
* The system shall support event-near notifications for eligible booked users.
* Notification processing shall be asynchronous where practical.
* The system shall send ticket delivery/booking confirmation emails through a third-party email service.
* The system shall notify affected users when an event/show is edited or cancelled, where applicable.
* Notification failure shall not change a successfully stored booking or payment state.

### 7.7 Organizer Dashboard
* Statistics shall include bookings, tickets sold, remaining capacity and sales amount.
* An organizer shall not see another organizer's dashboard data.
* The system shall provide organizers with a dashboard containing dynamic statistics for their own events.

## 8. Non-Functional Requirements
* Deployed client-server communication shall use HTTPS.
* No seat/capacity unit shall be allocated to more than one customer concurrently.
* Expired holds shall be released automatically within the configured hold period.
* Event consumers shall handle duplicate delivery without duplicate business effects.
* Each service shall validate authorization for its protected endpoints.
* Credentials, API keys and connection strings shall not be stored in source control.
* Each service shall provide health checks and structured logs.
* The system shall provide application monitoring and telemetry.
* The system shall be testable using unit tests, Selenium and JMeter.
* The system shall support concurrent booking attempts without duplicate allocation.
* The frontend shall be responsive on desktop and mobile browsers.
* Important payment, booking and event handlers shall be idempotent.
* The deployed system shall support horizontal scaling of stateless API instances where required.
* The system shall use Apache Kafka for asynchronous event streaming between relevant microservices.

## 9. Scope
Account management; organizer approval; event/venue/show management; promotions; event discovery; seat/capacity inventory; temporary holds; waiting room; bookings and cancellations; waitlists; payments and refunds; ticket issuance and validation; asynchronous email notifications; organizer dashboard statistics.

## 10. Limitations
Payment is limited to sandbox/test mode. Authentication and email depend on third-party providers. Ticket validation uses a manually entered unique ID/code. The waiting room uses a defined admission policy. Venue seating is represented using structured data rather than a full visual editor. The project is limited to the four-sprint implementation period and available Azure resources.

## 11. User Stories
| ID | User Story |
| :--- | :--- |
| **US01** | As a user, I want to browse and search published events so that I can find an event to attend. |
| **US02** | As a user, I want to view ticket/seat availability so that I can choose available inventory. |
| **US03** | As a user, I want to join a waiting room for a high-demand show so that access to ticket sales is controlled fairly. |
| **US04** | As a user, I want to join a waitlist for sold-out inventory so that I can purchase it if tickets become available. |
| **US05** | As a user, I want to temporarily hold selected tickets so that I have time to complete payment. |
| **US06** | As a user, I want to pay for a booking and receive a unique ticket code so that I can use my ticket. |
| **US07** | As a user, I want to cancel an eligible booking so that I can receive a refund when allowed. |
| **US08** | As a user, I want to receive event and ticket notifications so that I know about important changes. |
| **US09** | As an organizer, I want to submit an organizer request so that I can become an approved event organizer. |
| **US10** | As an organizer, I want to create and publish events and shows so that customers can purchase tickets. |
| **US11** | As an organizer, I want to create promotions so that I can offer discounts for my events. |
| **US12** | As an organizer, I want to view event sales statistics so that I can monitor performance. |
| **US13** | As an organizer, I want to validate ticket codes so that used/invalid tickets cannot be accepted again. |
| **US14** | As an administrator, I want to approve or reject organizer requests so that only approved organizers can sell tickets. |
| **US15** | As an administrator, I want to manage venues and seating definitions so that organizers can use valid venues. |
| **US16** | As an administrator, I want to suspend an organizer so that problematic accounts cannot continue selling. |

## 12. Business Rules
* Only approved organizers may publish or sell events.
* The administrator owns platform venues and seating/capacity definitions.
* A show cannot sell tickets before its configured on-sale time.
* A user cannot exceed the organizer-defined ticket limit.
* Inventory is authoritative for seat/ticket availability and holds.
* A hold expires after its configured period and becomes available again.
* A confirmed booking requires successful payment.
* Waiting-room users are admitted in arrival order while the queue is active.
* Waitlist offers are made in arrival order and expire after a defined period.
* Promotion codes apply only to their configured events and validity periods.
* Organizer data is tenant-isolated.
* Payment callbacks and business events must be idempotent.
* Notification failure must not roll back an already confirmed booking.
* Each confirmed ticket has a unique ticket ID/code and can only be marked used once.
* Inventory holds shall be created synchronously through the Inventory Service and shall return an immediate success or failure response.

## 13. Basic System Architecture
React Frontend  
↓  
API Gateway  
↓  
Kafka  

**Six Independent Services**
1. **Identity Service:** accounts, roles, organizer approval requests
2. **Catalog Service:** events, venues, shows, promotions
3. **Inventory Service:** seats/capacity, holds, waiting room
4. **Booking Service:** bookings, waitlist, tickets and validation
5. **Payment Service:** transactions and refunds
6. **Notification Service:** ticket delivery, event-change and event-near notifications

## 14. Technology Stack Summary
| Area | Technology |
| :--- | :--- |
| **Frontend** | React.js |
| **Backend** | ASP.NET Core |
| **Data Access** | ADO.NET with direct SQL |
| **Database** | PostgreSQL; database/schema ownership per service |
| **Event Management** | Apache Kafka |
| **Containerization** | Docker |
| **Cloud** | Azure App Service for Linux containers + Azure Container Registry |
| **CI/CD** | GitHub Actions |
| **Monitoring** | Azure Application Insights + structured logs + health endpoints |
| **Testing** | xUnit, integration tests, Selenium E2E and JMeter load tests |
| **Authentication** | Third-party identity provider |
| **Payment** | Third-party sandbox payment gateways |
| **Email** | Third-party email service |
| **API** | Versioned REST APIs with Swagger/OpenAPI |

<br><br>

---

# TicketHive - Technical Implementation Plan
**Project:** TicketHive  
**Description:** Multi Tenant Ticketing Platform for Live Events  
**Date:** Aug 16, 2026  

---

## 1. Introduction
TicketHive is a web-based multi-event ticket booking platform for events and movies. It allows users to discover events, join high-demand queues or waitlists, reserve tickets, make payments and receive ticket codes. Organizers create and manage events and promotions and view basic dashboard statistics. Platform administrators manage accounts and venues.

## 2. Architecture Principles
The six services are separated by business responsibility and data ownership. The design avoids a long synchronous chain. REST is used for operations that need an immediate response; asynchronous events are used for business state changes and notifications. Each service owns its own data.

## 3. Service Boundaries
| Service | Owns | Responsibilities |
| :--- | :--- | :--- |
| **Identity** | Accounts, roles, organizer approval requests | Application accounts, role mapping, organizer approval workflow and account status. |
| **Catalog** | Events, venues, shows, promotions | Event lifecycle, venue definitions, show schedules, ticket categories/prices and promotion rules. |
| **Inventory** | Seats/capacity, holds, waiting room | Authoritative availability, seat state, temporary holds/expiry, high-demand waiting room and admission tokens. |
| **Booking** | Bookings, waitlist, tickets | Booking lifecycle, cancellation, waitlist, ticket ID/code issuance and one-time validation. |
| **Payment** | Transactions, refunds | Third-party payment integration, payment state, idempotent callbacks and refunds. |
| **Notification** | Notification jobs | Ticket delivery, event-change/cancellation and event-near notifications via third-party email. |

## 4. Decoupling Strategy
The six services are separated by business responsibility and data ownership. The Inventory Service is authoritative for ticket/seat availability and temporary holds. Booking Service communicates with Inventory Service synchronously when requesting a hold because the booking flow requires an immediate success or failure response. Each service owns its own PostgreSQL database and no service directly accesses another service's database.

Identity is not called for every request: authenticated identity and role claims are carried in access tokens. Catalog does not own inventory state. Inventory receives required show/seat configuration through APIs/events rather than reading Catalog tables. Booking does not update Inventory tables; it requests inventory actions and receives results. Payment publishes payment results rather than directly changing Booking records. Notification consumes events and is outside the booking transaction.

## 5. Event Handling
| Event | Producer | Purpose |
| :--- | :--- | :--- |
| **OrganizerApproved** | Identity | Enables organizer operations in Catalog. |
| **EventPublished / EventUpdated** | Catalog | Updates relevant inventory configuration and triggers notification workflows. |
| **HoldExpired / Inventory Released** | Inventory | Notifies Booking of expired or released inventory and supports waitlist processing. |
| **PaymentRequested** | Booking | Requests Payment processing. |
| **PaymentSucceeded / Failed** | Payment | Tell Booking the payment result. |
| **BookingConfirmed** | Booking | Triggers notification and downstream actions. |
| **TicketIssued** | Booking | Triggers ticket delivery notification. |
| **BookingCancelled / Refund Requested** | Booking | Triggers refund and user notification workflow. |
| **RefundCompleted** | Payment | Notifies relevant services that a refund has been completed. |

*Important event properties:* unique event ID, event type, aggregate ID, timestamp and correlation ID. Consumers must be idempotent so duplicate event delivery does not duplicate tickets, refunds or state changes.

## 6. End-to-End Booking Flow
1. The user enters the purchase path.
2. The user selects inventory.
3. Booking creates a pending booking.
4. Booking sends an API request to Inventory to hold the selected inventory.
5. Inventory atomically changes inventory to HELD and emits success/failure.
6. If the hold succeeds, Booking requests Payment processing.
7. Payment records the transaction and emits success/failure.
8. Booking confirms/fails the booking and creates unique ticket codes on success.
9. Booking emits TicketIssued/BookingConfirmed.
10. Notification sends email asynchronously.
11. Inventory finalizes confirmed held inventory as SOLD.

## 7. Frontend
React.js provides User, Organizer and Platform Administrator interfaces. Customer pages cover discovery, event details, waiting room, seat/ticket selection, waitlist, checkout, payment result, bookings and ticket code. Organizer pages cover event/show management, promotions and dashboard. Administrator pages cover account approval and venue management.
Availability and queue state can be refreshed using controlled polling. Browser state is never authoritative for inventory.

## 8. Backend
ASP.NET Core Web API is used for all six services. Each service has controllers, business logic, ADO.NET data access, configuration, health endpoint and Swagger/OpenAPI. APIs use versioned JSON REST endpoints.

## 9. Database - PostgreSQL
| Service | Example Data |
| :--- | :--- |
| **Identity** | Accounts, roles, organizer requests, account status, audit records |
| **Catalog** | Events, shows, venues, seating definitions, ticket categories, promotions |
| **Inventory** | Inventory units, status, holds, expiry, waiting-room entries, admission tokens |
| **Booking** | Bookings, booking items, waitlist entries, offers, tickets, validation status |
| **Payment** | Transactions, payment attempts, refunds |
| **Notification** | Notification jobs, delivery status, retry state |

Each service is the only service allowed to read/write its database. Cross-service consistency is handled by service APIs/events rather than distributed database transactions.

## 10. Waiting Room
The waiting room is part of Inventory because it protects scarce inventory. A configured threshold activates the queue. Users are recorded in arrival order and admitted using short-lived tokens. Queue state is persistent rather than held only in process memory, so multiple Inventory instances can serve requests.

## 11. Waitlist
Waitlist records belong to Booking. When Inventory reports released inventory, Booking selects the earliest eligible entry and creates a time-limited offer. Expired offers are skipped and the next eligible user is considered.

## 12. Authentication & Authorization
A third-party identity provider handles authentication. Identity Service stores application account, role and organizer approval state. Services validate the authenticated identity and enforce resource ownership.

## 13. Payment
Payment Service uses a third-party sandbox/test gateway. Card details are not stored. Payment callbacks are idempotent and mapped to unique payment/booking references. Payment results are published as events.

## 14. Notification
Notification Service consumes TicketIssued, EventUpdated, EventCancelled and event-near events. It sends email through a third-party service and stores delivery status. Failures are retried without changing booking/payment state.

## 15. Ticket IDs
Booking Service issues a unique ticket ID/code after successful payment. Validation checks event/show association and used state, then marks a successful ticket as USED.

## 16. Organizer Dashboard
The organizer dashboard is the dynamic reporting feature. It can show bookings, tickets sold, remaining capacity and sales amount using service APIs or maintained summary data.

## 17. API Gateway
A single API entry point provides frontend routing and a stable external boundary. It should not contain business rules. Service-level authorization remains in the services.

## 18. Containerization
Docker will containerize the React frontend and all six ASP.NET services. Docker Compose will support repeatable local development. Images are versioned by commit/build identifier.

## 19. Azure Deployment
Recommended Azure components are Azure App Service for Linux containers, Azure Container Registry, Azure Event Hubs and Azure Application Insights.

## 20. Load Balancing & Horizontal Scaling
Azure App Service provides platform-level load distribution when multiple instances of a stateless API service are running. The API entry point routes requests without storing session state in one instance.
Inventory state is not stored in application memory. PostgreSQL is the authoritative source for seat availability and holds, allowing multiple Inventory Service instances to process concurrent requests safely.
Apache Kafka consumer groups allow event-processing workloads to be distributed across multiple instances of the same consumer service.

## 21. Monitoring & Observability
Each service exposes a health endpoint and structured logs. Monitor API latency/errors, booking failures, inventory hold failures, queue depth, payment failures, notification failures and event-processing failures. Correlation IDs should be propagated through requests and events.

## 22. CI/CD
| Stage | Implementation |
| :--- | :--- |
| **Checkout** | Pull source from GitHub. |
| **Restore/Build** | Restore dependencies and build frontend/services. |
| **Unit Tests** | Run xUnit tests and publish results/coverage. |
| **Quality Checks** | Run configured static/format/dependency checks. |
| **Container Build** | Build Docker images. |
| **Registry** | Push successful images to Azure Container Registry. |
| **Deploy** | Deploy images to Azure App Service. |
| **Smoke Test** | Call health endpoints and key smoke APIs. |
| **Observe** | Verify Application Insights telemetry and deployment status. |

## 23. Testing Strategy
* **xUnit**: Business logic and rules such as inventory transitions, hold expiry, queue admission, waitlist ordering, payment idempotency and ticket validation.
* **Selenium**: Customer booking journey, organizer flows and administrator flows.
* **JMeter**: Concurrent browsing, queue admission and especially concurrent hold attempts. Verify response time, throughput, error rate and absence of duplicate allocation.

## 24. Security & Reliability
HTTPS; third-party authentication; service-level authorization; organizer tenant isolation; no card storage; secrets outside source control; input validation; idempotent payment/event handling; local database transactions for critical state; health checks; retries for asynchronous notifications; and appropriate failure-state handling.

## 25. API Documentation
Swagger will document each service's endpoints, request/response models, authentication requirements and error responses.

## 26. Four Sprint Roadmap
| Sprint | Focus | Output |
| :--- | :--- | :--- |
| **1** | Foundation, Identity, Catalog, React, databases, Docker, CI/CD, Azure baseline | Working event/catalog slice deployed through CI/CD. |
| **2** | Inventory, holds, waiting room, Booking, waitlist, concurrency testing | Controlled-access booking flow with protected inventory. |
| **3** | Payment, refunds, tickets, notifications, promotions, event-driven workflows, monitoring | Complete purchase-to-ticket and notification flow. |
| **4** | Integration, dashboard, Selenium/JMeter, coverage, security, reliability, deployment hardening | Mail functionality, Refund System & Stable final system. |
