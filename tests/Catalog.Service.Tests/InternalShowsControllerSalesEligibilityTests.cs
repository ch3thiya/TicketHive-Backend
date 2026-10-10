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

public class InternalShowsControllerSalesEligibilityTests
{
    private readonly Mock<ISalesEligibilityService> _eligibility = new();
    private readonly InternalShowsController _controller;

    public InternalShowsControllerSalesEligibilityTests()
    {
        _controller = new InternalShowsController(Mock.Of<IEventService>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    private void Returns(Guid showId, SalesEligibilityOutcome outcome) =>
        _eligibility.Setup(e => e.CheckAsync(showId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SalesEligibilityResult(showId, outcome));

    [Fact]
    public async Task GetSalesEligibility_Eligible_ReturnsOkTrue()
    {
        // Arrange
        var showId = Guid.NewGuid();
        Returns(showId, SalesEligibilityOutcome.Eligible);

        // Act
        var result = await _controller.GetSalesEligibility(showId, _eligibility.Object);

        // Assert
        var body = Assert.IsType<SalesEligibilityResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.True(body.Eligible);
    }

    [Theory]
    [InlineData(SalesEligibilityOutcome.OrganizerSuspended, "OrganizerSuspended")]
    [InlineData(SalesEligibilityOutcome.ShowNotOnSale, "ShowNotOnSale")]
    [InlineData(SalesEligibilityOutcome.OrganizerNotActive, "OrganizerNotActive")]
    public async Task GetSalesEligibility_NotEligible_ReturnsOkFalseWithReason(SalesEligibilityOutcome outcome, string reason)
    {
        // Arrange
        var showId = Guid.NewGuid();
        Returns(showId, outcome);

        // Act
        var result = await _controller.GetSalesEligibility(showId, _eligibility.Object);

        // Assert
        var body = Assert.IsType<SalesEligibilityResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.False(body.Eligible);
        Assert.Equal(reason, body.Reason);
    }

    [Fact]
    public async Task GetSalesEligibility_OrganizerStatusUnavailable_Returns503NotEligible()
    {
        // Arrange
        var showId = Guid.NewGuid();
        Returns(showId, SalesEligibilityOutcome.OrganizerStatusUnavailable);

        // Act
        var result = await _controller.GetSalesEligibility(showId, _eligibility.Object);

        // Assert
        Assert.Equal(503, Assert.IsType<ObjectResult>(result).StatusCode);
    }

    [Fact]
    public async Task GetSalesEligibility_UnknownShow_Returns404()
    {
        // Arrange
        var showId = Guid.NewGuid();
        Returns(showId, SalesEligibilityOutcome.ShowNotFound);

        // Act
        var result = await _controller.GetSalesEligibility(showId, _eligibility.Object);

        // Assert
        Assert.Equal(404, Assert.IsType<ObjectResult>(result).StatusCode);
    }

    [Fact]
    public void GetSalesEligibility_RequiresInternalServicePolicy()
    {
        // Arrange
        var method = typeof(InternalShowsController).GetMethod(nameof(InternalShowsController.GetSalesEligibility))!;

        // Act
        var authorize = method.GetCustomAttributes<AuthorizeAttribute>().Single();

        // Assert
        Assert.Equal("InternalService", authorize.Policy);
    }
}