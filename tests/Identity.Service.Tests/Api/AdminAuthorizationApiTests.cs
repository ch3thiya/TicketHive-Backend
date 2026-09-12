using System.Net;
using Moq;
using Xunit;
using Identity.Service.Db;

namespace Identity.Service.Tests.Api;

public class AdminAuthorizationApiTests : IClassFixture<AdminApiFactory>
{
    private readonly AdminApiFactory _factory;

    public AdminAuthorizationApiTests(AdminApiFactory factory)
    {
        _factory = factory;
        _factory.AccountRepositoryMock.Reset();
        _factory.ScimClientMock.Reset();
    }

    [Fact]
    public async Task RejectRequest_NoToken_ReturnsUnauthorized()
    {
        // Arrange
        var client = _factory.CreateClient();

        // Act
        var response = await client.PostAsync($"/api/identity/organizer-requests/{Guid.NewGuid()}/reject", content: null);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        _factory.AccountRepositoryMock.Verify(
            r => r.UpdateOrganizerRequestStatusAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task RejectRequest_NonAdmin_ReturnsForbidden()
    {
        // Arrange
        var requestId = Guid.NewGuid();
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/identity/organizer-requests/{requestId}/reject");
        request.Headers.Add(TestAuthHandler.SubHeaderName, "customer-sub");
        request.Headers.Add(TestAuthHandler.RoleHeaderName, "Customer");

        var client = _factory.CreateClient();

        // Act
        var response = await client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        _factory.AccountRepositoryMock.Verify(
            r => r.UpdateOrganizerRequestStatusAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }
}
