using System.Diagnostics.Metrics;
using Inventory.Service.Clients;
using Inventory.Service.Db;
using Inventory.Service.Models;
using Inventory.Service.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Inventory.Service.Tests;

public class SeatHoldServiceTests : IDisposable
{
    private readonly Mock<IHoldRepository> _repository = new();
    private readonly Mock<ISalesEligibilityClient> _eligibility = new();
    private readonly Meter _meter = new("seat-hold-tests");
    private readonly SeatHoldMetrics _metrics;
    private readonly HoldService _service;
    private readonly Guid _show = Guid.NewGuid();
    private readonly List<(string Name, long Value)> _measurements = new();
    private readonly MeterListener _listener = new();

    public SeatHoldServiceTests()
    {
        _metrics = new SeatHoldMetrics(_meter);
        _eligibility.Setup(c => c.CheckAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SalesEligibilityDecision(SalesEligibilityStatus.Eligible));
        _repository.Setup(r => r.GetShowRulesAsync(_show)).ReturnsAsync(new ShowRules
        {
            ShowId = _show,
            OrganizerId = Guid.NewGuid(),
            MaxPerCustomer = 6,
            HoldMinutes = 10,
            HighDemand = false
        });
        _service = new HoldService(_repository.Object, new FakeTimeProvider(new DateTimeOffset(2026, 10, 11, 10, 0, 0, TimeSpan.Zero)),
            new Mock<ILogger<HoldService>>().Object, new Mock<IAdmissionTokenVerifier>().Object, _eligibility.Object, _metrics);

        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter == _meter) listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => _measurements.Add((instrument.Name, value)));
        _listener.Start();
    }

    public void Dispose()
    {
        _listener.Dispose();
        _meter.Dispose();
    }

    private static List<Guid> Seats(int count) => Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToList();

    [Fact]
    public async Task SeatsAndItemsTogether_AreRefusedBeforeAnythingIsWritten()
    {
        var request = new CreateHoldRequest(_show, new List<CreateHoldItemRequest> { new(Guid.NewGuid(), 1) }, Seats(1));

        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateHoldAsync("c", "k", false, request));

        _repository.Verify(r => r.CreateAsync(It.IsAny<Hold>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task NeitherSeatsNorItems_IsRefused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateHoldAsync("c", "k", false, new CreateHoldRequest(_show)));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateHoldAsync("c", "k", false, new CreateHoldRequest(_show, null, new List<Guid>())));
    }

    [Fact]
    public async Task DuplicateEmptyOrTooManySeats_AreRefused()
    {
        var seat = Guid.NewGuid();

        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateHoldAsync("c", "k", false, new CreateHoldRequest(_show, null, new List<Guid> { seat, seat })));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateHoldAsync("c", "k", false, new CreateHoldRequest(_show, null, new List<Guid> { Guid.Empty })));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateHoldAsync("c", "k", false, new CreateHoldRequest(_show, null, Seats(51))));
        _repository.Verify(r => r.CreateAsync(It.IsAny<Hold>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task ASeatHold_IsPassedToTheRepositoryWithOnlyTheSeatIds_AndCountedInMetrics()
    {
        var ids = Seats(2);
        _repository.Setup(r => r.CreateAsync(It.IsAny<Hold>(), 6)).ReturnsAsync((Hold hold, int _) => new HoldCreationResult
        {
            Outcome = HoldCreationOutcome.Created,
            Hold = new Hold
            {
                Id = hold.Id,
                ShowId = hold.ShowId,
                CustomerSub = "c",
                Status = HoldStatus.Active,
                ExpiresAt = hold.ExpiresAt,
                Seats = hold.Seats.Select((s, i) => new HoldSeat(s.SeatId, Guid.NewGuid(), "Front", "A", i + 1)).ToList()
            }
        });

        var result = await _service.CreateHoldAsync("c", "k", false, new CreateHoldRequest(_show, null, ids));

        Assert.Equal(CreateHoldStatus.Created, result.Status);
        Assert.Equal(2, result.Hold!.Seats!.Count);
        _repository.Verify(r => r.CreateAsync(It.Is<Hold>(h => h.Items.Count == 0 && h.Seats.Select(s => s.SeatId).SequenceEqual(ids)), 6), Times.Once);
        Assert.Contains(("inventory.seat_holds.seats_held", 2L), _measurements);
    }

    [Fact]
    public async Task UnavailableSeats_AreReportedWithTheirLabels_AndCountedAsARefusal()
    {
        var taken = new HoldSeat(Guid.NewGuid(), Guid.NewGuid(), "Hall", "B", 4);
        _repository.Setup(r => r.CreateAsync(It.IsAny<Hold>(), It.IsAny<int>()))
            .ReturnsAsync(new HoldCreationResult { Outcome = HoldCreationOutcome.SeatsUnavailable, UnavailableSeats = new[] { taken } });

        var result = await _service.CreateHoldAsync("c", "k", false, new CreateHoldRequest(_show, null, Seats(2)));

        Assert.Equal(CreateHoldStatus.SeatsUnavailable, result.Status);
        var seat = Assert.Single(result.UnavailableSeats!);
        Assert.Equal(("Hall", "B", 4), (seat.Section, seat.Row, seat.Number));
        Assert.Contains(("inventory.seat_holds.selections_refused", 1L), _measurements);
        Assert.DoesNotContain(_measurements, m => m.Name == "inventory.seat_holds.seats_held");
    }

    [Theory]
    [InlineData(HoldCreationOutcome.SeatsRequired, CreateHoldStatus.SeatsRequired)]
    [InlineData(HoldCreationOutcome.SeatsNotSupported, CreateHoldStatus.SeatsNotSupported)]
    public async Task ModeMismatches_MapToTheirStatuses(HoldCreationOutcome outcome, CreateHoldStatus expected)
    {
        _repository.Setup(r => r.CreateAsync(It.IsAny<Hold>(), It.IsAny<int>())).ReturnsAsync(new HoldCreationResult { Outcome = outcome });

        var result = await _service.CreateHoldAsync("c", "k", false, new CreateHoldRequest(_show, null, Seats(1)));

        Assert.Equal(expected, result.Status);
    }

    [Fact]
    public void TheInternalHoldResponse_CarriesTheSeatsBookingNeeds()
    {
        var hold = new Hold
        {
            Id = Guid.NewGuid(),
            ShowId = _show,
            CustomerSub = "c",
            Status = HoldStatus.Active,
            ExpiresAt = DateTimeOffset.UtcNow,
            Seats = new List<HoldSeat> { new(Guid.NewGuid(), Guid.NewGuid(), "Front", "A", 3) }
        };

        var response = _service.ToInternalResponse(hold);

        var seat = Assert.Single(response.Seats!);
        Assert.Equal(("Front", "A", 3), (seat.Section, seat.Row, seat.Number));
        Assert.Equal(hold.Seats[0].SeatId, seat.SeatId);
    }
}
