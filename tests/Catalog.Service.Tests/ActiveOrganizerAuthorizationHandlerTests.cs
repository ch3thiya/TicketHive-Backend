using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Catalog.Service.Authorization;
using Catalog.Service.Clients;

namespace Catalog.Service.Tests;

public class ActiveOrganizerAuthorizationHandlerTests
{
    private readonly Mock<IOrganizerStatusClient> _mockClient;
    private readonly Mock<IHttpContextAccessor> _mockHttpContextAccessor;
    private readonly DefaultHttpContext _httpContext;
    private readonly ActiveOrganizerAuthorizationHandler _handler;

    public ActiveOrganizerAuthorizationHandlerTests()
    {
        _mockClient = new Mock<IOrganizerStatusClient>();
        _httpContext = new DefaultHttpContext();
        _mockHttpContextAccessor = new Mock<IHttpContextAccessor>();
        _mockHttpContextAccessor.Setup(a => a.HttpContext).Returns(_httpContext);

        _handler = new ActiveOrganizerAuthorizationHandler(
            _mockClient.Object,
            _mockHttpContextAccessor.Object,
            Mock.Of<ILogger<ActiveOrganizerAuthorizationHandler>>());
    }

    private static AuthorizationHandlerContext CreateContext(ClaimsPrincipal user)
    {
        return new AuthorizationHandlerContext(
            new[] { new ActiveOrganizerRequirement() },
            user,
            resource: null);
    }

    private static ClaimsPrincipal UserWithSub(string sub)
    {
        var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, sub) }, "TestAuth");
        return new ClaimsPrincipal(identity);
    }

    [Fact]
    public async Task HandleRequirementAsync_ActiveOrganizer_SucceedsAndStashesOrganizerId()
    {
        // Arrange
        var organizerId = Guid.NewGuid();
        _mockClient.Setup(c => c.GetOrganizerStatusAsync("sub-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrganizerLookupResult(OrganizerLookupStatus.Active, organizerId));
        var context = CreateContext(UserWithSub("sub-1"));

        // Act
        await _handler.HandleAsync(context);

        // Assert
        Assert.True(context.HasSucceeded);
        Assert.Equal(organizerId, _httpContext.Items[ActiveOrganizerAuthorizationHandler.OrganizerIdItemKey]);
    }

    [Fact]
    public async Task HandleRequirementAsync_UnknownUser_FailsRequirement()
    {
        // Arrange
        _mockClient.Setup(c => c.GetOrganizerStatusAsync("sub-customer", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrganizerLookupResult(OrganizerLookupStatus.NotFound, null));
        var context = CreateContext(UserWithSub("sub-customer"));

        // Act
        await _handler.HandleAsync(context);

        // Assert
        Assert.False(context.HasSucceeded);
        Assert.False(_httpContext.Items.ContainsKey(ActiveOrganizerAuthorizationHandler.IdentityUnavailableItemKey));
    }

    [Fact]
    public async Task HandleRequirementAsync_IdentityUnavailable_FailsAndFlagsIdentityUnavailable()
    {
        // Arrange
        _mockClient.Setup(c => c.GetOrganizerStatusAsync("sub-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrganizerLookupResult(OrganizerLookupStatus.Unavailable, null));
        var context = CreateContext(UserWithSub("sub-1"));

        // Act
        await _handler.HandleAsync(context);

        // Assert: must never allow the write through when Identity cannot be reached.
        Assert.False(context.HasSucceeded);
        Assert.True((bool)_httpContext.Items[ActiveOrganizerAuthorizationHandler.IdentityUnavailableItemKey]!);
    }

    [Fact]
    public async Task HandleRequirementAsync_NoSubClaim_FailsRequirementWithoutCallingClient()
    {
        // Arrange
        var context = CreateContext(new ClaimsPrincipal(new ClaimsIdentity()));

        // Act
        await _handler.HandleAsync(context);

        // Assert
        Assert.False(context.HasSucceeded);
        _mockClient.Verify(c => c.GetOrganizerStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
