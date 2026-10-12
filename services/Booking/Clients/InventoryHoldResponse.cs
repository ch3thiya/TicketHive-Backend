using System;
using System.Collections.Generic;

namespace Booking.Service.Clients;

public record InventoryHoldItemResponse(Guid CategoryId, int Quantity, decimal UnitPrice, string Currency);

public record InventoryHoldResponse(Guid HoldId, Guid ShowId, string CustomerSub, string Status, DateTimeOffset ExpiresAt, List<InventoryHoldItemResponse> Items, List<InventoryHoldSeatResponse>? Seats = null);

public record InventoryHoldSeatResponse(Guid SeatId, Guid CategoryId, string Section, string Row, int Number);
