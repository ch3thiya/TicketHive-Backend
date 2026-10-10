using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;
using Booking.Service.Clients;
using Booking.Service.Controllers;
using Booking.Service.Db;
using Booking.Service.Models;
using Booking.Service.Services;

namespace Booking.Service.Tests;

// Suspending an organizer must not touch tickets that were already sold. Booking has no
// dependency on organizer status at all, so owners keep access and entry validation keeps
// working; these tests pin that down.
public class TicketsDuringSuspensionTests
{
    private readonly Mock<ITicketService> _tickets = new();
    private readonly Mock<ITicketRepository> _repository = new();
    private readonly Mock<IEntryAccessClient> _entryAccess = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 10, 8, 0, 0, TimeSpan.Zero));
    private readonly TicketsController _controller;

    public TicketsDuringSuspensionTests()
    {
        // The owning organizer is allowed whether or not the account is suspended: Catalog treats
        // both statuses as owners.
        _entryAccess.Setup(a => a.CheckAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(EntryAccessDecision.Allowed);
        _controller = new TicketsController(_tickets.Object, _repository.Object, _entryAccess.Object, _time, NullLogger<TicketsController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "caller-sub")], "test"))
                }
            }
        };
    }

    private static Ticket ValidTicket(string code = "CODE-1") => new()
    {
        Id = Guid.NewGuid(),
        OrderId = Guid.NewGuid(),
        CategoryId = Guid.NewGuid(),
        ShowId = Guid.NewGuid(),
        CustomerSub = "customer-1",
        UniqueCode = code,
        Price = 50m,
        IssuedAt = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero)
    };

    [Fact]
    public void TicketsController_DoesNotDependOnAnyOrganizerOrSalesStatusService()
    {
        // Arrange
        var dependencies = typeof(TicketsController).GetConstructors().Single().GetParameters().Select(p => p.ParameterType.Name);
        var allTypes = typeof(TicketsController).Assembly.GetTypes().Select(t => t.Name);

        // Assert
        Assert.DoesNotContain(dependencies, name => name.Contains("Organizer") || name.Contains("SalesEligibility"));
        Assert.Contains(dependencies, name => name == nameof(IEntryAccessClient));
        Assert.DoesNotContain(allTypes, name => name.Contains("OrganizerStatus") || name.Contains("SalesEligibility"));
    }

    [Fact]
    public async Task ValidateTicket_ValidTicket_IsAcceptedAndMarkedUsed()
    {
        // Arrange
        var ticket = ValidTicket();
        _repository.Setup(r => r.GetByCodeAsync(ticket.UniqueCode)).ReturnsAsync(ticket);
        _repository.Setup(r => r.ValidateAndUseTicketAsync(ticket.UniqueCode, "caller-sub", _time.GetUtcNow())).ReturnsAsync(true);

        // Act
        var result = await _controller.ValidateTicket(ticket.UniqueCode);

        // Assert
        Assert.IsType<OkObjectResult>(result);
        _repository.Verify(r => r.ValidateAndUseTicketAsync(ticket.UniqueCode, "caller-sub", _time.GetUtcNow()), Times.Once);
    }

    [Fact]
    public async Task ValidateTicket_VoidedTicket_IsStillRefused()
    {
        // Arrange: only a real cancellation voids a ticket, never a suspension.
        var ticket = ValidTicket();
        ticket.VoidedAt = _time.GetUtcNow();
        _repository.Setup(r => r.GetByCodeAsync(ticket.UniqueCode)).ReturnsAsync(ticket);

        // Act
        var result = await _controller.ValidateTicket(ticket.UniqueCode);

        // Assert
        Assert.Equal(409, Assert.IsType<ObjectResult>(result).StatusCode);
        _repository.Verify(r => r.ValidateAndUseTicketAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>()), Times.Never);
    }

    [Fact]
    public async Task GetCustomerTickets_ReturnsTheCustomersTickets()
    {
        // Arrange
        IReadOnlyList<TicketResponse> tickets = [];
        _tickets.Setup(t => t.GetCustomerTicketsAsync("caller-sub")).ReturnsAsync(tickets);

        // Act
        var result = await _controller.GetCustomerTickets();

        // Assert
        Assert.IsType<OkObjectResult>(result);
    }
}