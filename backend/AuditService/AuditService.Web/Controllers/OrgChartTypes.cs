using AuditService.Domain;

namespace AuditService.Web.Controllers;

/// <param name="At">The instant the snapshot is as of (a time in the future is clamped to now).</param>
/// <param name="FirstEventAt">Where the recorded history starts, for a date slider; null when nothing is recorded.</param>
/// <param name="Unplaced">People whose last recorded department did not exist at that moment.</param>
/// <param name="SkippedEvents">Events of a known type that could not be read, so a gap in the log is visible.</param>
public record OrgChartResponse(
    DateTime At,
    DateTime? FirstEventAt,
    IReadOnlyList<OrgDepartment> Departments,
    IReadOnlyList<OrgPerson> Unplaced,
    int SkippedEvents);

public class OrgChartOptions
{
    public const int DefaultMaxEvents = 50_000;

    public int MaxEvents { get; set; } = DefaultMaxEvents;
}
