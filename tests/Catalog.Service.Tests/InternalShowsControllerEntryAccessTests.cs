using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;
using Catalog.Service.Controllers;
using Catalog.Service.Models;
using Catalog.Service.Services;

namespace Catalog.Service.Tests;

public class InternalShowsControllerEntryAccessTests
{
    private readonly Mock<IEntryAccessService> _access = new();
    private readonly InternalShowsController _controller;

    public InternalShowsControllerEntryAccessTests()
    {
        _controller = new InternalShowsController(Mock.Of<IEventService>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    private void Returns(Guid showId, string sub, EntryAccessOutcome outcome) =>
        _access.Setup(a => a.CheckAsync(showId, sub, It.IsAny<CancellationToken>())).ReturnsAsync(new EntryAccessResult(showId, outcome));

    [Fact]
    public async Task GetEntryAccess_Allowed_ReturnsOkTrue()
    {
        // Arrange
        var showId = Guid.NewGuid();
        Returns(showId, "owner", EntryAccessOutcome.Allowed);

        // Act
        var result = await _controller.GetEntryAccess(showId, "owner", _access.Object);

        // Assert
        var body = Assert.IsType<EntryAccessResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.True(body.Allowed);
    }

    [Theory]
    [InlineData(EntryAccessOutcome.NotShowOwner, "NotShowOwner")]
    [InlineData(EntryAccessOutcome.NotAnOrganizer, "NotAnOrganizer")]
    public async Task GetEntryAccess_Refused_ReturnsOkFalseWithReason(EntryAccessOutcome outcome, string reason)
    {
        // Arrange
        var showId = Guid.NewGuid();
        Returns(showId, "caller", outcome);

        // Act
        var result = await _controller.GetEntryAccess(showId, "caller", _access.Object);

        // Assert
        var body = Assert.IsType<EntryAccessResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.False(body.Allowed);
        Assert.Equal(reason, body.Reason);
    }

    [Fact]
    public async Task GetEntryAccess_OrganizerStatusUnavailable_Returns503()
    {
        // Arrange
        var showId = Guid.NewGuid();
        Returns(showId, "owner", EntryAccessOutcome.OrganizerStatusUnavailable);

        // Act
        var result = await _controller.GetEntryAccess(showId, "owner", _access.Object);

        // Assert
        Assert.Equal(503, Assert.IsType<ObjectResult>(result).StatusCode);
    }

    [Fact]
    public async Task GetEntryAccess_UnknownShow_Returns404()
    {
        // Arrange
        var showId = Guid.NewGuid();
        Returns(showId, "owner", EntryAccessOutcome.ShowNotFound);

        // Act
        var result = await _controller.GetEntryAccess(showId, "owner", _access.Object);

        // Assert
        Assert.Equal(404, Assert.IsType<ObjectResult>(result).StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task GetEntryAccess_MissingSub_Returns400AndAsksNobody(string? sub)
    {
        // Act
        var result = await _controller.GetEntryAccess(Guid.NewGuid(), sub, _access.Object);

        // Assert
        Assert.Equal(400, Assert.IsType<ObjectResult>(result).StatusCode);
        _access.Verify(a => a.CheckAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void GetEntryAccess_RequiresInternalServicePolicy()
    {
        // Arrange
        var method = typeof(InternalShowsController).GetMethod(nameof(InternalShowsController.GetEntryAccess))!;

        // Act
        var authorize = method.GetCustomAttributes<AuthorizeAttribute>().Single();

        // Assert
        Assert.Equal("InternalService", authorize.Policy);
    }
}