using System;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
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

public class TicketValidationAuthorizationTests
{
    private readonly Mock<ITicketRepository> _repository = new();
    private readonly Mock<IEntryAccessClient> _entryAccess = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 10, 8, 0, 0, TimeSpan.Zero));

    private TicketsController ControllerFor(string role)
    {
        return new TicketsController(Mock.Of<ITicketService>(), _repository.Object, _entryAccess.Object, _time, NullLogger<TicketsController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, "caller-sub"), new Claim(ClaimTypes.Role, role)], "test"))
                }
            }
        };
    }

    private static Ticket NewTicket(DateTimeOffset? usedAt = null, DateTimeOffset? voidedAt = null) => new()
    {
        Id = Guid.NewGuid(),
        OrderId = Guid.NewGuid(),
        CategoryId = Guid.NewGuid(),
        ShowId = Guid.NewGuid(),
        CustomerSub = "customer-1",
        UniqueCode = "CODE-1",
        Price = 50m,
        IssuedAt = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
        UsedAt = usedAt,
        UsedBy = usedAt is null ? null : "someone-elses-sub",
        VoidedAt = voidedAt
    };

    private Ticket Seed(Ticket? ticket = null)
    {
        ticket ??= NewTicket();
        _repository.Setup(r => r.GetByCodeAsync(ticket.UniqueCode)).ReturnsAsync(ticket);
        _repository.Setup(r => r.ValidateAndUseTicketAsync(ticket.UniqueCode, "caller-sub", It.IsAny<DateTimeOffset>())).ReturnsAsync(true);
        return ticket;
    }

    private void Access(EntryAccessDecision decision) =>
        _entryAccess.Setup(a => a.CheckAsync(It.IsAny<Guid>(), "caller-sub", It.IsAny<CancellationToken>())).ReturnsAsync(decision);

    private void VerifyNothingWritten() =>
        _repository.Verify(r => r.ValidateAndUseTicketAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>()), Times.Never);

    [Fact]
    public void ValidateTicket_RequiresAnOrganizerOrAdminGroup_AndNeverACustomer()
    {
        // Arrange
        var method = typeof(TicketsController).GetMethod(nameof(TicketsController.ValidateTicket))!;

        // Act
        var roles = method.GetCustomAttributes<AuthorizeAttribute>().Single().Roles!.Split(',');

        // Assert
        Assert.Contains("Organizer", roles);
        Assert.Contains("Admin", roles);
        Assert.DoesNotContain(roles, r => r.Contains("Customer", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ValidateTicket_OwningOrganizer_ValidatesTheTicket()
    {
        // Arrange
        var ticket = Seed();
        Access(EntryAccessDecision.Allowed);

        // Act
        var result = await ControllerFor("Organizer").ValidateTicket(ticket.UniqueCode);

        // Assert
        Assert.IsType<OkObjectResult>(result);
        _entryAccess.Verify(a => a.CheckAsync(ticket.ShowId, "caller-sub", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ValidateTicket_OrganizerOfAnotherShow_Gets403AndNothingIsWritten()
    {
        // Arrange
        var ticket = Seed();
        Access(EntryAccessDecision.Denied);

        // Act
        var result = await ControllerFor("Organizer").ValidateTicket(ticket.UniqueCode);

        // Assert
        Assert.Equal(403, Assert.IsType<ObjectResult>(result).StatusCode);
        VerifyNothingWritten();
    }

    [Fact]
    public async Task ValidateTicket_CatalogCannotVerify_Gets503WithRetryAfterAndNothingIsWritten()
    {
        // Arrange
        var ticket = Seed();
        Access(EntryAccessDecision.Unavailable);
        var controller = ControllerFor("Organizer");

        // Act
        var result = await controller.ValidateTicket(ticket.UniqueCode);

        // Assert
        Assert.Equal(503, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.True(controller.Response.Headers.ContainsKey("Retry-After"));
        VerifyNothingWritten();
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("admin")]
    [InlineData("Admins")]
    public async Task ValidateTicket_Admin_MayValidateAnyShowWithoutAskingCatalog(string role)
    {
        // Arrange
        var ticket = Seed();

        // Act
        var result = await ControllerFor(role).ValidateTicket(ticket.UniqueCode);

        // Assert
        Assert.IsType<OkObjectResult>(result);
        _entryAccess.Verify(a => a.CheckAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ValidateTicket_RepeatedScans_AskCatalogEveryTime()
    {
        // Arrange: ownership is never cached.
        var ticket = Seed();
        Access(EntryAccessDecision.Allowed);
        var controller = ControllerFor("Organizer");

        // Act
        await controller.ValidateTicket(ticket.UniqueCode);
        await controller.ValidateTicket(ticket.UniqueCode);

        // Assert
        _entryAccess.Verify(a => a.CheckAsync(ticket.ShowId, "caller-sub", It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidateTicket_RefusedCaller_LearnsNothingAboutUsedOrVoidedTickets(bool voided)
    {
        // Arrange
        var now = _time.GetUtcNow();
        var ticket = Seed(voided ? NewTicket(voidedAt: now) : NewTicket(usedAt: now));
        Access(EntryAccessDecision.Denied);

        // Act
        var result = await ControllerFor("Organizer").ValidateTicket(ticket.UniqueCode);

        // Assert: a plain 403, not the 409 that would reveal the ticket's state.
        Assert.Equal(403, Assert.IsType<ObjectResult>(result).StatusCode);
    }

    [Fact]
    public async Task ValidateTicket_AlreadyUsed_ReportsTheTimeButNeverWhoScannedIt()
    {
        // Arrange
        var ticket = Seed(NewTicket(usedAt: _time.GetUtcNow()));
        Access(EntryAccessDecision.Allowed);

        // Act
        var result = await ControllerFor("Organizer").ValidateTicket(ticket.UniqueCode);

        // Assert
        var problem = Assert.IsType<ProblemDetails>(Assert.IsType<ObjectResult>(result).Value);
        Assert.Equal(409, problem.Status);
        Assert.DoesNotContain("someone-elses-sub", problem.Detail);
        Assert.Contains("already used at", problem.Detail);
    }

    [Fact]
    public async Task ValidateTicket_VoidedTicketOfOwner_IsStillRefusedAfterCancellation()
    {
        // Arrange
        var ticket = Seed(NewTicket(voidedAt: _time.GetUtcNow()));
        Access(EntryAccessDecision.Allowed);

        // Act
        var result = await ControllerFor("Organizer").ValidateTicket(ticket.UniqueCode);

        // Assert
        Assert.Equal(409, Assert.IsType<ObjectResult>(result).StatusCode);
        VerifyNothingWritten();
    }

    [Fact]
    public async Task ValidateTicket_UnknownCode_Returns404WithoutAskingCatalogAndWithoutEchoingTheCode()
    {
        // Arrange
        _repository.Setup(r => r.GetByCodeAsync("NOPE")).ReturnsAsync((Ticket?)null);

        // Act
        var result = await ControllerFor("Organizer").ValidateTicket("NOPE");

        // Assert
        var problem = Assert.IsType<ProblemDetails>(Assert.IsType<ObjectResult>(result).Value);
        Assert.Equal(404, problem.Status);
        Assert.DoesNotContain("NOPE", problem.Detail);
        _entryAccess.Verify(a => a.CheckAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}