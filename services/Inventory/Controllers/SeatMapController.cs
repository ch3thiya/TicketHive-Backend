using Inventory.Service.Db;
using Inventory.Service.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Inventory.Service.Controllers;

// Public, like availability: seat states carry no customer data. Never cached (ADR-027); the
// gateway's per-client rate limit applies, and clients poll as they do for availability.
[ApiController]
[AllowAnonymous]
[Route("api/inventory/shows/{showId:guid}/seats")]
public class SeatMapController(ISeatLayoutRepository seats) : ControllerBase
{
    public record SeatDto(Guid Id, int Number, Guid CategoryId, string State);

    public record RowDto(string Label, List<SeatDto> Seats);

    public record SectionDto(string Name, List<RowDto> Rows);

    public record SeatMapResponse(Guid ShowId, int Version, IReadOnlyList<SeatMapCategory> Categories, List<SectionDto> Sections);

    public record SeatStatesResponse(Guid ShowId, List<SeatState> Unavailable);

    [HttpGet]
    [ProducesResponseType(typeof(SeatMapResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSeatMap(Guid showId)
    {
        NoStore();
        var map = await seats.GetSeatMapAsync(showId);
        if (map is null)
        {
            return Problem(detail: "This show does not have a seat map.", statusCode: StatusCodes.Status404NotFound, title: "No seat map");
        }

        var sections = new List<SectionDto>();
        foreach (var bySection in map.Seats.GroupBy(s => s.Section))
        {
            var rows = bySection
                .GroupBy(s => s.Row)
                .Select(row => new RowDto(row.Key, row.Select(s => new SeatDto(s.Id, s.Number, s.CategoryId, s.State)).ToList()))
                .ToList();
            sections.Add(new SectionDto(bySection.Key, rows));
        }

        return Ok(new SeatMapResponse(map.ShowId, map.Version, map.Categories, sections));
    }

    // Only the seats that are not available, so a poll stays small while a show is mostly open.
    [HttpGet("states")]
    [ProducesResponseType(typeof(SeatStatesResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSeatStates(Guid showId)
    {
        NoStore();
        var states = await seats.GetUnavailableSeatsAsync(showId);
        if (states is null)
        {
            return Problem(detail: "This show does not have a seat map.", statusCode: StatusCodes.Status404NotFound, title: "No seat map");
        }

        return Ok(new SeatStatesResponse(showId, states.ToList()));
    }

    private void NoStore()
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
    }
}
