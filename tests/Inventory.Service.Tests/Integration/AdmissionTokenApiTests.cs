using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Npgsql;
using Xunit;
using Inventory.Service.Tests.Api;

namespace Inventory.Service.Tests.Integration;

// ADR-014 test-first: written and skipped before HoldService verifies
// admission tokens locally (it still gates on the now-deleted waiting-room
// repository), then unskipped in "test: add admission gate tests" once the
// verifier, the deletion and the table-drop migration have all landed.
// Real Postgres via Testcontainers and real HTTP requests through the
// actual pipeline — the thing under test is the gate's status-code
// behaviour end to end, not a mocked verifier.
[Collection("HoldsApi")]
public sealed class AdmissionTokenApiTests
{
    private readonly HoldsApiFixture _fixture;

    public AdmissionTokenApiTests(HoldsApiFixture fixture) => _fixture = fixture;

    [Fact(Skip = "Unskipped in 'test: add admission gate tests' (SCRUM-11)")]
    public async Task CreateHold_HighDemandShow_NoAdmissionTokenHeader_ReturnsForbidden()
    {
        // Arrange
        var showId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        await SeedShowAsync(showId, categoryId, capacity: 10, maxPerCustomer: 6, highDemand: true);

        // Act
        var response = await PostHoldAsync(showId, categoryId, customerSub: "customer-1", idempotencyKey: "key-1", admissionToken: null);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact(Skip = "Unskipped in 'test: add admission gate tests' (SCRUM-11)")]
    public async Task CreateHold_HighDemandShow_ValidToken_Succeeds()
    {
        // Arrange
        var showId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        await SeedShowAsync(showId, categoryId, capacity: 10, maxPerCustomer: 6, highDemand: true);
        var token = AdmissionTokenTestKeys.MintToken(showId, "customer-1", DateTimeOffset.UtcNow);

        // Act
        var response = await PostHoldAsync(showId, categoryId, customerSub: "customer-1", idempotencyKey: "key-1", admissionToken: token);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact(Skip = "Unskipped in 'test: add admission gate tests' (SCRUM-11)")]
    public async Task CreateHold_HighDemandShow_ExpiredToken_ReturnsForbidden()
    {
        // Arrange
        var showId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        await SeedShowAsync(showId, categoryId, capacity: 10, maxPerCustomer: 6, highDemand: true);
        var token = AdmissionTokenTestKeys.MintToken(showId, "customer-1", DateTimeOffset.UtcNow.AddMinutes(-20), lifetime: TimeSpan.FromMinutes(5));

        // Act
        var response = await PostHoldAsync(showId, categoryId, customerSub: "customer-1", idempotencyKey: "key-1", admissionToken: token);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact(Skip = "Unskipped in 'test: add admission gate tests' (SCRUM-11)")]
    public async Task CreateHold_HighDemandShow_TokenForDifferentShow_ReturnsForbidden()
    {
        // Arrange
        var showId = Guid.NewGuid();
        var otherShowId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        await SeedShowAsync(showId, categoryId, capacity: 10, maxPerCustomer: 6, highDemand: true);
        var token = AdmissionTokenTestKeys.MintToken(otherShowId, "customer-1", DateTimeOffset.UtcNow);

        // Act
        var response = await PostHoldAsync(showId, categoryId, customerSub: "customer-1", idempotencyKey: "key-1", admissionToken: token);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact(Skip = "Unskipped in 'test: add admission gate tests' (SCRUM-11)")]
    public async Task CreateHold_HighDemandShow_TokenSignedByDifferentKey_ReturnsForbidden()
    {
        // Arrange
        var showId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        await SeedShowAsync(showId, categoryId, capacity: 10, maxPerCustomer: 6, highDemand: true);
        using var wrongKey = RSA.Create(2048);
        var token = AdmissionTokenTestKeys.MintToken(showId, "customer-1", DateTimeOffset.UtcNow, signAs: wrongKey);

        // Act
        var response = await PostHoldAsync(showId, categoryId, customerSub: "customer-1", idempotencyKey: "key-1", admissionToken: token);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact(Skip = "Unskipped in 'test: add admission gate tests' (SCRUM-11)")]
    public async Task CreateHold_HighDemandShow_MalformedToken_ReturnsForbidden()
    {
        // Arrange
        var showId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        await SeedShowAsync(showId, categoryId, capacity: 10, maxPerCustomer: 6, highDemand: true);

        // Act
        var response = await PostHoldAsync(showId, categoryId, customerSub: "customer-1", idempotencyKey: "key-1", admissionToken: "not-a-jwt");

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact(Skip = "Unskipped in 'test: add admission gate tests' (SCRUM-11)")]
    public async Task CreateHold_HighDemandShow_TokenForDifferentCustomer_ReturnsForbidden()
    {
        // Arrange
        var showId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        await SeedShowAsync(showId, categoryId, capacity: 10, maxPerCustomer: 6, highDemand: true);
        var token = AdmissionTokenTestKeys.MintToken(showId, "someone-else", DateTimeOffset.UtcNow);

        // Act
        var response = await PostHoldAsync(showId, categoryId, customerSub: "customer-1", idempotencyKey: "key-1", admissionToken: token);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact(Skip = "Unskipped in 'test: add admission gate tests' (SCRUM-11)")]
    public async Task CreateHold_HighDemandShow_AllRejectionReasons_ReturnIndistinguishableProblemDetails()
    {
        // Arrange — the four ways a hold can be rejected at the gate: no
        // header, a token for the wrong show, a token for the wrong
        // customer, and a token signed by a key the server doesn't trust.
        var showId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        await SeedShowAsync(showId, categoryId, capacity: 10, maxPerCustomer: 6, highDemand: true);
        using var wrongKey = RSA.Create(2048);

        var noHeader = await PostHoldAsync(showId, categoryId, "customer-1", "key-no-header", admissionToken: null);
        var wrongShow = await PostHoldAsync(showId, categoryId, "customer-1", "key-wrong-show",
            admissionToken: AdmissionTokenTestKeys.MintToken(Guid.NewGuid(), "customer-1", DateTimeOffset.UtcNow));
        var wrongCustomer = await PostHoldAsync(showId, categoryId, "customer-1", "key-wrong-customer",
            admissionToken: AdmissionTokenTestKeys.MintToken(showId, "someone-else", DateTimeOffset.UtcNow));
        var wrongKeySignature = await PostHoldAsync(showId, categoryId, "customer-1", "key-wrong-key",
            admissionToken: AdmissionTokenTestKeys.MintToken(showId, "customer-1", DateTimeOffset.UtcNow, signAs: wrongKey));

        // Act
        var bodies = await Task.WhenAll(
            noHeader.Content.ReadAsStringAsync(),
            wrongShow.Content.ReadAsStringAsync(),
            wrongCustomer.Content.ReadAsStringAsync(),
            wrongKeySignature.Content.ReadAsStringAsync());

        // Assert — same status and the same title/detail, so a caller can't
        // tell which reason caused the rejection.
        Assert.All(new[] { noHeader, wrongShow, wrongCustomer, wrongKeySignature },
            r => Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode));

        var problems = Array.ConvertAll(bodies, body => JsonSerializer.Deserialize<JsonElement>(body));
        var (firstTitle, firstDetail) = (problems[0].GetProperty("title").GetString(), problems[0].GetProperty("detail").GetString());
        Assert.All(problems, p =>
        {
            Assert.Equal(firstTitle, p.GetProperty("title").GetString());
            Assert.Equal(firstDetail, p.GetProperty("detail").GetString());
        });
    }

    [Fact(Skip = "Unskipped in 'test: add admission gate tests' (SCRUM-11)")]
    public async Task CreateHold_NotHighDemandShow_SucceedsRegardlessOfAdmissionToken()
    {
        // Arrange
        var showId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        await SeedShowAsync(showId, categoryId, capacity: 10, maxPerCustomer: 6, highDemand: false);

        // Act — no token at all.
        var withoutToken = await PostHoldAsync(showId, categoryId, "customer-1", "key-a", admissionToken: null);

        // Act — a token that wouldn't even verify (malformed), still ignored.
        var withJunkToken = await PostHoldAsync(showId, categoryId, "customer-2", "key-b", admissionToken: "not-a-jwt");

        // Assert
        Assert.Equal(HttpStatusCode.Created, withoutToken.StatusCode);
        Assert.Equal(HttpStatusCode.Created, withJunkToken.StatusCode);
    }

    private async Task<HttpResponseMessage> PostHoldAsync(Guid showId, Guid categoryId, string customerSub, string idempotencyKey, string? admissionToken)
    {
        var client = _fixture.Factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/inventory/holds")
        {
            Content = JsonContent.Create(new
            {
                showId,
                items = new[] { new { categoryId, quantity = 1 } }
            })
        };
        request.Headers.Add(CustomerTestAuthHandler.SubHeaderName, customerSub);
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        if (admissionToken is not null)
        {
            request.Headers.Add("Admission-Token", admissionToken);
        }

        return await client.SendAsync(request);
    }

    private async Task SeedShowAsync(Guid showId, Guid categoryId, int capacity, int maxPerCustomer, bool highDemand)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();

        await using (var command = new NpgsqlCommand(
            """
            INSERT INTO show_rules (show_id, organizer_id, max_per_customer, hold_minutes, high_demand)
            VALUES (@ShowId, @OrganizerId, @MaxPerCustomer, 10, @HighDemand);
            """, connection))
        {
            command.Parameters.AddWithValue("ShowId", showId);
            command.Parameters.AddWithValue("OrganizerId", Guid.NewGuid());
            command.Parameters.AddWithValue("MaxPerCustomer", maxPerCustomer);
            command.Parameters.AddWithValue("HighDemand", highDemand);
            await command.ExecuteNonQueryAsync();
        }

        await using (var command = new NpgsqlCommand(
            """
            INSERT INTO stock (show_id, category_id, capacity, available, unit_price, currency)
            VALUES (@ShowId, @CategoryId, @Capacity, @Capacity, 50.00, 'LKR');
            """, connection))
        {
            command.Parameters.AddWithValue("ShowId", showId);
            command.Parameters.AddWithValue("CategoryId", categoryId);
            command.Parameters.AddWithValue("Capacity", capacity);
            await command.ExecuteNonQueryAsync();
        }
    }
}
