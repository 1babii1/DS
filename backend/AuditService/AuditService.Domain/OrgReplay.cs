using System.Text.Json;

namespace AuditService.Domain;

/// <summary>One recorded event, as far as the replay is concerned: what happened, its body, when, and its arrival order.</summary>
/// <param name="Order">Breaks ties between events with the same time: the order they were recorded in.</param>
public sealed record HistoricEvent(string Type, string Payload, DateTime OccurredAt, long Order);

public sealed record OrgPerson(Guid Id, string Name, string Position);

public sealed record OrgDepartment(
    Guid Id, Guid? ParentId, string Name, string Identifier, int Depth, IReadOnlyList<OrgPerson> People);

/// <param name="Unplaced">People whose last recorded department did not exist at that moment (it had been deleted, or was never seen).</param>
/// <param name="SkippedEvents">Events of a known type that could not be read; a number, not a failure, so a gap is visible.</param>
public sealed record OrgSnapshot(
    DateTime At, IReadOnlyList<OrgDepartment> Departments, IReadOnlyList<OrgPerson> Unplaced, int SkippedEvents);

// The organization as it was at an instant, rebuilt by folding the recorded events up to it, in the order they
// happened. Nothing is stored: the log is the only source, so the answer can only be as complete as the log is. An
// event about something never seen (a rename of an unknown department) is ignored rather than invented. Only what a
// directory shows is kept - names and positions, never contact details.
public static class OrgReplay
{
    public const string DepartmentCreated = "DepartmentCreated";
    public const string DepartmentRenamed = "DepartmentRenamed";
    public const string DepartmentMoved = "DepartmentMoved";
    public const string DepartmentDeleted = "DepartmentDeleted";
    public const string PositionCreated = "PositionCreated";
    public const string EmployeeHired = "EmployeeHired";
    public const string EmployeeTransferred = "EmployeeTransferred";
    public const string EmployeeTerminated = "EmployeeTerminated";

    // The event types the fold reads; the query that feeds it selects exactly these.
    public static readonly IReadOnlyList<string> EventTypes =
    [
        DepartmentCreated, DepartmentRenamed, DepartmentMoved, DepartmentDeleted,
        PositionCreated, EmployeeHired, EmployeeTransferred, EmployeeTerminated,
    ];

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private sealed record Dept(string Name, string Identifier, Guid? ParentId);

    private sealed record Emp(string Name, Guid DepartmentId, Guid PositionId);

    public static OrgSnapshot At(DateTime at, IEnumerable<HistoricEvent> events)
    {
        var departments = new Dictionary<Guid, Dept>();
        var positions = new Dictionary<Guid, string>();
        var employees = new Dictionary<Guid, Emp>();
        var skipped = 0;

        foreach (var e in events.Where(e => e.OccurredAt <= at).OrderBy(e => e.OccurredAt).ThenBy(e => e.Order))
        {
            try
            {
                using var doc = JsonDocument.Parse(e.Payload);
                if (!Apply(e.Type, doc.RootElement, departments, positions, employees))
                {
                    skipped++;
                }
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or FormatException or InvalidOperationException)
            {
                skipped++;
            }
        }

        return Build(at, departments, positions, employees, skipped);
    }

    // False means the event was of a known type but its body could not be used.
    private static bool Apply(
        string type,
        JsonElement body,
        Dictionary<Guid, Dept> departments,
        Dictionary<Guid, string> positions,
        Dictionary<Guid, Emp> employees)
    {
        switch (type)
        {
            case DepartmentCreated:
                departments[Id(body, "DepartmentId")] = new Dept(
                    Text(body, "Name"), Text(body, "Identifier"), OptionalId(body, "ParentDepartmentId"));
                return true;

            case DepartmentRenamed:
                if (departments.TryGetValue(Id(body, "DepartmentId"), out var renamed))
                {
                    departments[Id(body, "DepartmentId")] = renamed with { Name = Text(body, "Name"), Identifier = Text(body, "Identifier") };
                }

                return true;

            case DepartmentMoved:
                if (departments.TryGetValue(Id(body, "DepartmentId"), out var moved))
                {
                    departments[Id(body, "DepartmentId")] = moved with { ParentId = OptionalId(body, "NewParentDepartmentId") };
                }

                return true;

            case DepartmentDeleted:
                departments.Remove(Id(body, "DepartmentId"));
                return true;

            case PositionCreated:
                positions[Id(body, "PositionId")] = Text(body, "Name");
                return true;

            case EmployeeHired:
                employees[Id(body, "EmployeeId")] = new Emp(
                    Text(body, "FullName"), Id(body, "DepartmentId"), Id(body, "PositionId"));
                return true;

            case EmployeeTransferred:
                if (employees.TryGetValue(Id(body, "EmployeeId"), out var transferred))
                {
                    employees[Id(body, "EmployeeId")] = transferred with
                    {
                        DepartmentId = Id(body, "DepartmentId"),
                        PositionId = Id(body, "PositionId"),
                    };
                }

                return true;

            case EmployeeTerminated:
                employees.Remove(Id(body, "EmployeeId"));
                return true;

            default:
                return true;
        }
    }

    private static OrgSnapshot Build(
        DateTime at,
        Dictionary<Guid, Dept> departments,
        Dictionary<Guid, string> positions,
        Dictionary<Guid, Emp> employees,
        int skipped)
    {
        OrgPerson Person(Guid id, Emp e) =>
            new(id, e.Name, positions.GetValueOrDefault(e.PositionId, "Unknown position"));

        var byDepartment = employees
            .Where(pair => departments.ContainsKey(pair.Value.DepartmentId))
            .GroupBy(pair => pair.Value.DepartmentId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<OrgPerson>)g.Select(p => Person(p.Key, p.Value)).OrderBy(p => p.Name, StringComparer.Ordinal).ToList());

        var nodes = departments
            .Select(pair => new OrgDepartment(
                pair.Key,

                // A parent that does not exist at that moment (deleted, or never seen) leaves the department a root.
                pair.Value.ParentId is { } parent && departments.ContainsKey(parent) ? parent : null,
                pair.Value.Name,
                pair.Value.Identifier,
                Depth(pair.Key, departments),
                byDepartment.GetValueOrDefault(pair.Key, [])))
            .OrderBy(d => d.Depth)
            .ThenBy(d => d.Name, StringComparer.Ordinal)
            .ThenBy(d => d.Id)
            .ToList();

        var unplaced = employees
            .Where(pair => !departments.ContainsKey(pair.Value.DepartmentId))
            .Select(pair => Person(pair.Key, pair.Value))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToList();

        return new OrgSnapshot(at, nodes, unplaced, skipped);
    }

    // Steps up the parent chain that exists at that moment; a cycle (which the source forbids but a log could hold) stops.
    private static int Depth(Guid id, Dictionary<Guid, Dept> departments)
    {
        var depth = 0;
        var seen = new HashSet<Guid> { id };
        var current = id;
        while (departments[current].ParentId is { } parent && departments.ContainsKey(parent) && seen.Add(parent))
        {
            depth++;
            current = parent;
        }

        return depth;
    }

    private static Guid Id(JsonElement body, string name) => body.GetProperty(PropertyName(body, name)).GetGuid();

    private static Guid? OptionalId(JsonElement body, string name) =>
        body.TryGetProperty(PropertyName(body, name), out var v) && v.ValueKind == JsonValueKind.String ? v.GetGuid() : null;

    private static string Text(JsonElement body, string name) => body.GetProperty(PropertyName(body, name)).GetString()
        ?? throw new FormatException($"{name} is null");

    // Producers write PascalCase; a case-insensitive match keeps the fold from caring how a body was formatted.
    private static string PropertyName(JsonElement body, string name) =>
        body.EnumerateObject().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).Name ?? name;
}
