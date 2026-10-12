namespace Catalog.Service.Models;

public record VenueSeatMapSection(string Name, int Rows, int SeatsPerRow);

public record VenueSeatMapSpec(IReadOnlyList<VenueSeatMapSection> Sections);

public record VenueSeatMap(
    Guid VenueId,
    VenueSeatMapSpec Spec,
    int SeatCount,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public static class VenueSeatMapRules
{
    public static int SeatCount(VenueSeatMapSpec spec) => spec.Sections.Sum(section => section.Rows * section.SeatsPerRow);

    public static IReadOnlyList<LayoutError> Validate(VenueSeatMapSpec? spec, int venueCapacity)
    {
        var errors = new List<LayoutError>();
        if (spec?.Sections is not { Count: > 0 })
        {
            errors.Add(new LayoutError("NoSections", "The seat map needs at least one section."));
            return errors;
        }

        if (spec.Sections.Count > SeatLayoutRules.MaxSections)
        {
            errors.Add(new LayoutError("TooLarge", $"A seat map can have at most {SeatLayoutRules.MaxSections} sections."));
            return errors;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var section in spec.Sections)
        {
            var name = section.Name?.Trim() ?? string.Empty;
            if (name.Length == 0 || name.Length > SeatLayoutRules.MaxSectionNameLength)
            {
                errors.Add(new LayoutError("InvalidSectionName", $"Each section needs a name of 1 to {SeatLayoutRules.MaxSectionNameLength} characters."));
            }
            else if (!names.Add(name))
            {
                errors.Add(new LayoutError("DuplicateSection", $"Section '{name}' is defined more than once."));
            }

            if (section.Rows < 1 || section.Rows > SeatLayoutRules.MaxRowsPerSection ||
                section.SeatsPerRow < 1 || section.SeatsPerRow > SeatLayoutRules.MaxSeatsPerRow)
            {
                errors.Add(new LayoutError("InvalidDimensions",
                    $"Section '{(name.Length == 0 ? "(unnamed)" : name)}' needs 1 to {SeatLayoutRules.MaxRowsPerSection} rows and 1 to {SeatLayoutRules.MaxSeatsPerRow} seats per row."));
                continue;
            }

            total += (long)section.Rows * section.SeatsPerRow;
        }

        if (total > SeatLayoutRules.MaxSeats || total > venueCapacity)
        {
            errors.Add(new LayoutError("CapacityExceeded", $"The seat map has {total} seats but the venue capacity is {venueCapacity}."));
        }

        return errors;
    }
}
