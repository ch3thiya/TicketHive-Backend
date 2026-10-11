namespace Catalog.Service.Models;

public static class SeatMapStatuses
{
    public const string Pending = "Pending";
    public const string Completed = "Completed";
    public const string Rejected = "Rejected";
}

public static class SeatLayoutStatuses
{
    public const string Draft = "Draft";
    public const string Enabled = "Enabled";
    public const string Superseded = "Superseded";
}

public static class SeatingModes
{
    public const string GeneralAdmission = "GA";
    public const string Seated = "Seated";
}

public record SeatMapRequest(
    Guid Id,
    Guid ShowId,
    Guid OrganizerId,
    string Description,
    string Status,
    string? RejectionReason,
    DateTimeOffset RequestedAt,
    DateTimeOffset? DecidedAt,
    string? DecidedBy);

public record SeatLayoutSection(string Name, Guid CategoryId, int Rows, int SeatsPerRow);

public record SeatLayoutSpec(IReadOnlyList<SeatLayoutSection> Sections);

public record SeatLayout(
    Guid Id,
    Guid ShowId,
    Guid RequestId,
    int Version,
    string Status,
    SeatLayoutSpec Spec,
    int SeatCount,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? EnabledAt);

public record SeatPosition(Guid CategoryId, string Section, string Row, int Number);

public record LayoutError(string Code, string Message);

/// <summary>
/// Validation and expansion of an admin-defined layout. Row labels run A..Z, AA, AB, ... and seat
/// numbers run 1..n per row, so a section cannot produce duplicate seats on its own.
/// </summary>
public static class SeatLayoutRules
{
    public const int MaxSections = 100;
    public const int MaxRowsPerSection = 200;
    public const int MaxSeatsPerRow = 500;
    public const int MaxSeats = 50_000;
    public const int MaxSectionNameLength = 50;

    public static string RowLabel(int index)
    {
        var label = string.Empty;
        for (var n = index; n >= 0; n = (n / 26) - 1)
        {
            label = (char)('A' + (n % 26)) + label;
        }

        return label;
    }

    public static int SeatCount(SeatLayoutSpec spec) => spec.Sections.Sum(s => s.Rows * s.SeatsPerRow);

    public static IReadOnlyList<LayoutError> Validate(SeatLayoutSpec? spec, IReadOnlyList<TicketCategory> categories)
    {
        var errors = new List<LayoutError>();
        if (spec is null || spec.Sections is null || spec.Sections.Count == 0)
        {
            errors.Add(new LayoutError("NoSections", "The layout needs at least one section."));
            return errors;
        }

        if (spec.Sections.Count > MaxSections)
        {
            errors.Add(new LayoutError("TooLarge", $"A layout can have at most {MaxSections} sections."));
            return errors;
        }

        var active = categories.Where(c => c.IsActive).ToDictionary(c => c.Id);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var perCategory = new Dictionary<Guid, long>();
        long total = 0;

        foreach (var section in spec.Sections)
        {
            var name = section.Name?.Trim() ?? string.Empty;
            if (name.Length == 0 || name.Length > MaxSectionNameLength)
            {
                errors.Add(new LayoutError("InvalidSectionName", $"Each section needs a name of 1 to {MaxSectionNameLength} characters."));
            }
            else if (!names.Add(name))
            {
                errors.Add(new LayoutError("DuplicateSection", $"Section '{name}' is defined more than once."));
            }

            var label = name.Length == 0 ? "(unnamed)" : name;
            if (section.Rows < 1 || section.Rows > MaxRowsPerSection || section.SeatsPerRow < 1 || section.SeatsPerRow > MaxSeatsPerRow)
            {
                errors.Add(new LayoutError("InvalidDimensions",
                    $"Section '{label}' needs 1 to {MaxRowsPerSection} rows and 1 to {MaxSeatsPerRow} seats per row."));
                continue;
            }

            var seats = (long)section.Rows * section.SeatsPerRow;
            total += seats;

            if (section.CategoryId == Guid.Empty)
            {
                errors.Add(new LayoutError("UnassignedSection", $"Section '{label}' has no ticket category assigned."));
                continue;
            }

            if (!active.ContainsKey(section.CategoryId))
            {
                errors.Add(new LayoutError("UnknownCategory", $"Section '{label}' uses a ticket category that does not belong to this show."));
                continue;
            }

            perCategory[section.CategoryId] = perCategory.GetValueOrDefault(section.CategoryId) + seats;
        }

        if (total > MaxSeats)
        {
            errors.Add(new LayoutError("TooLarge", $"A layout can have at most {MaxSeats} seats."));
        }

        if (errors.Count > 0)
        {
            return errors;
        }

        foreach (var category in active.Values)
        {
            var mapped = perCategory.GetValueOrDefault(category.Id);
            if (mapped != category.Capacity)
            {
                errors.Add(new LayoutError("CategoryCapacityMismatch",
                    $"Category '{category.Name}' has capacity {category.Capacity} but {mapped} seats are mapped to it."));
            }
        }

        var showTotal = active.Values.Sum(c => (long)c.Capacity);
        if (total != showTotal)
        {
            errors.Add(new LayoutError("TotalCapacityMismatch",
                $"The layout has {total} seats but the show's ticket total is {showTotal}."));
        }

        return errors;
    }

    public static IEnumerable<SeatPosition> Expand(SeatLayoutSpec spec)
    {
        foreach (var section in spec.Sections)
        {
            var name = section.Name.Trim();
            for (var row = 0; row < section.Rows; row++)
            {
                var label = RowLabel(row);
                for (var number = 1; number <= section.SeatsPerRow; number++)
                {
                    yield return new SeatPosition(section.CategoryId, name, label, number);
                }
            }
        }
    }
}
