using System.Text.Json;
using AuditService.Domain;

namespace AuditService.IntegrationTests;

// The plan's acceptance test: a sequence of hire, transfer, termination, rename, move and delete gives the right
// slice at every date in between, including exactly at an event and just before it.
public class OrgReplayTests
{
    private static readonly Guid Eng = Guid.NewGuid();
    private static readonly Guid Payments = Guid.NewGuid();
    private static readonly Guid Developer = Guid.NewGuid();
    private static readonly Guid Designer = Guid.NewGuid();
    private static readonly Guid Anna = Guid.NewGuid();
    private static readonly Guid Boris = Guid.NewGuid();

    private static DateTime D(int month, int day, int hour = 12) => new(2026, month, day, hour, 0, 0, DateTimeKind.Utc);

    private static long _order;

    private static HistoricEvent E(string type, DateTime at, object body) =>
        new(type, JsonSerializer.Serialize(body), at, Interlocked.Increment(ref _order));

    private static HistoricEvent[] History() =>
    [
        E("DepartmentCreated", D(1, 1), new { DepartmentId = Eng, Name = "Engineering", Identifier = "eng", ParentDepartmentId = (Guid?)null }),
        E("DepartmentCreated", D(1, 1), new { DepartmentId = Payments, Name = "Payments", Identifier = "payments", ParentDepartmentId = (Guid?)null }),
        E("PositionCreated", D(1, 1), new { PositionId = Developer, Name = "Developer", Description = (string?)null, DepartmentIds = new[] { Eng, Payments } }),
        E("PositionCreated", D(1, 1), new { PositionId = Designer, Name = "Designer", Description = (string?)null, DepartmentIds = new[] { Eng } }),
        E("EmployeeHired", D(1, 5), new { EmployeeId = Anna, FullName = "Anna Ivanova", Email = "anna@secret.example", DepartmentId = Payments, PositionId = Developer }),
        E("EmployeeHired", D(1, 10), new { EmployeeId = Boris, FullName = "Boris Kim", Email = "boris@secret.example", DepartmentId = Eng, PositionId = Developer }),
        E("DepartmentRenamed", D(2, 1), new { DepartmentId = Payments, Name = "Treasury", Identifier = "payments" }),
        E("EmployeeTransferred", D(2, 10), new { EmployeeId = Anna, DepartmentId = Eng, PositionId = Designer }),
        E("DepartmentMoved", D(3, 1), new { DepartmentId = Payments, OldParentDepartmentId = (Guid?)null, NewParentDepartmentId = Eng }),
        E("EmployeeTerminated", D(3, 15), new { EmployeeId = Boris }),
        E("DepartmentDeleted", D(4, 1), new { DepartmentId = Payments }),
    ];

    private static OrgDepartment Dept(OrgSnapshot s, Guid id) => s.Departments.Single(d => d.Id == id);

    private static string[] People(OrgSnapshot s, Guid id) => Dept(s, id).People.Select(p => $"{p.Name}/{p.Position}").ToArray();

    [Fact]
    public void Before_anything_happened_the_org_is_empty()
    {
        var snapshot = OrgReplay.At(D(1, 1, 0), History());

        Assert.Empty(snapshot.Departments);
        Assert.Empty(snapshot.Unplaced);
    }

    [Fact]
    public void Departments_exist_from_their_creation_and_positions_do_not_become_people()
    {
        var snapshot = OrgReplay.At(D(1, 1, 13), History());

        Assert.Equal(["Engineering", "Payments"], snapshot.Departments.Select(d => d.Name).ToArray());
        Assert.All(snapshot.Departments, d => Assert.Empty(d.People));
    }

    [Fact]
    public void A_hire_appears_exactly_at_its_time_and_not_a_moment_before()
    {
        Assert.Empty(People(OrgReplay.At(D(1, 5).AddTicks(-1), History()), Payments));
        Assert.Equal(["Anna Ivanova/Developer"], People(OrgReplay.At(D(1, 5), History()), Payments));
    }

    [Fact]
    public void A_rename_applies_from_its_time()
    {
        Assert.Equal("Payments", Dept(OrgReplay.At(D(1, 31), History()), Payments).Name);
        Assert.Equal("Treasury", Dept(OrgReplay.At(D(2, 1), History()), Payments).Name);
        Assert.Equal("payments", Dept(OrgReplay.At(D(2, 1), History()), Payments).Identifier);
    }

    [Fact]
    public void A_transfer_moves_the_person_and_their_position_together()
    {
        var before = OrgReplay.At(D(2, 9), History());
        var after = OrgReplay.At(D(2, 10), History());

        Assert.Equal(["Anna Ivanova/Developer"], People(before, Payments));
        Assert.Empty(People(after, Payments));
        Assert.Equal(["Anna Ivanova/Designer", "Boris Kim/Developer"], People(after, Eng));
    }

    [Fact]
    public void A_move_changes_the_parent_and_the_depth_from_its_time()
    {
        var before = OrgReplay.At(D(2, 28), History());
        var after = OrgReplay.At(D(3, 1), History());

        Assert.Null(Dept(before, Payments).ParentId);
        Assert.Equal(0, Dept(before, Payments).Depth);
        Assert.Equal(Eng, Dept(after, Payments).ParentId);
        Assert.Equal(1, Dept(after, Payments).Depth);
    }

    [Fact]
    public void A_termination_removes_the_person_from_that_time_on_and_not_before()
    {
        Assert.Contains("Boris Kim/Developer", People(OrgReplay.At(D(3, 14), History()), Eng));
        Assert.DoesNotContain("Boris Kim/Developer", People(OrgReplay.At(D(3, 15), History()), Eng));
    }

    [Fact]
    public void A_deleted_department_is_gone_from_its_time()
    {
        Assert.Contains(OrgReplay.At(D(3, 31), History()).Departments, d => d.Id == Payments);
        Assert.DoesNotContain(OrgReplay.At(D(4, 1), History()).Departments, d => d.Id == Payments);
    }

    [Fact]
    public void The_present_is_the_last_state()
    {
        var snapshot = OrgReplay.At(D(12, 31), History());

        Assert.Equal(["Engineering"], snapshot.Departments.Select(d => d.Name).ToArray());
        Assert.Equal(["Anna Ivanova/Designer"], People(snapshot, Eng));
    }

    [Fact]
    public void Events_are_folded_by_when_they_happened_not_by_the_order_they_arrived_in()
    {
        // Recorded in the opposite order to how they happened, as after an outage or a replay: what decides is the
        // time, so the arrival order (here reversed) must not.
        var late = History().Select((e, i) => e with { Order = 1000 - i }).ToArray();

        var snapshot = OrgReplay.At(D(2, 20), late);

        Assert.Equal("Treasury", Dept(snapshot, Payments).Name);
        Assert.Equal(["Anna Ivanova/Designer", "Boris Kim/Developer"], People(snapshot, Eng));
    }

    [Fact]
    public void Events_with_the_same_time_apply_in_the_order_they_were_recorded()
    {
        var events = new[]
        {
            E("DepartmentCreated", D(1, 1), new { DepartmentId = Eng, Name = "First", Identifier = "e", ParentDepartmentId = (Guid?)null }),
            E("DepartmentRenamed", D(1, 1), new { DepartmentId = Eng, Name = "Second", Identifier = "e" }),
        };

        Assert.Equal("Second", Dept(OrgReplay.At(D(1, 1), events), Eng).Name);
    }

    [Fact]
    public void Events_about_something_never_seen_are_ignored_not_invented()
    {
        var events = new[]
        {
            E("DepartmentRenamed", D(1, 1), new { DepartmentId = Eng, Name = "Ghost", Identifier = "g" }),
            E("EmployeeTransferred", D(1, 2), new { EmployeeId = Anna, DepartmentId = Eng, PositionId = Developer }),
            E("EmployeeTerminated", D(1, 3), new { EmployeeId = Boris }),
        };

        var snapshot = OrgReplay.At(D(2, 1), events);

        Assert.Empty(snapshot.Departments);
        Assert.Empty(snapshot.Unplaced);
        Assert.Equal(0, snapshot.SkippedEvents);
    }

    [Fact]
    public void A_person_whose_department_no_longer_exists_is_listed_as_unplaced_rather_than_lost()
    {
        var events = History().Where(e => e.Type != "EmployeeTransferred" && e.Type != "EmployeeTerminated").ToArray();

        var snapshot = OrgReplay.At(D(4, 2), events);

        Assert.Equal(["Anna Ivanova"], snapshot.Unplaced.Select(p => p.Name).ToArray());
    }

    [Fact]
    public void A_department_whose_parent_was_deleted_becomes_a_root()
    {
        var child = Guid.NewGuid();
        var events = new[]
        {
            E("DepartmentCreated", D(1, 1), new { DepartmentId = Eng, Name = "Parent", Identifier = "p", ParentDepartmentId = (Guid?)null }),
            E("DepartmentCreated", D(1, 2), new { DepartmentId = child, Name = "Child", Identifier = "c", ParentDepartmentId = (Guid?)Eng }),
            E("DepartmentDeleted", D(1, 3), new { DepartmentId = Eng }),
        };

        var node = Dept(OrgReplay.At(D(1, 4), events), child);

        Assert.Null(node.ParentId);
        Assert.Equal(0, node.Depth);
    }

    [Fact]
    public void A_position_with_no_recorded_name_is_shown_as_unknown()
    {
        var events = new[]
        {
            E("DepartmentCreated", D(1, 1), new { DepartmentId = Eng, Name = "Engineering", Identifier = "eng", ParentDepartmentId = (Guid?)null }),
            E("EmployeeHired", D(1, 2), new { EmployeeId = Anna, FullName = "Anna", Email = "a@x", DepartmentId = Eng, PositionId = Guid.NewGuid() }),
        };

        Assert.Equal(["Anna/Unknown position"], People(OrgReplay.At(D(1, 3), events), Eng));
    }

    [Fact]
    public void A_cycle_in_the_log_does_not_hang_the_fold()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var events = new[]
        {
            E("DepartmentCreated", D(1, 1), new { DepartmentId = a, Name = "A", Identifier = "a", ParentDepartmentId = (Guid?)null }),
            E("DepartmentCreated", D(1, 1), new { DepartmentId = b, Name = "B", Identifier = "b", ParentDepartmentId = (Guid?)a }),
            E("DepartmentMoved", D(1, 2), new { DepartmentId = a, OldParentDepartmentId = (Guid?)null, NewParentDepartmentId = b }),
        };

        var snapshot = OrgReplay.At(D(1, 3), events);

        Assert.Equal(2, snapshot.Departments.Count);
    }

    [Fact]
    public void An_unreadable_event_is_skipped_and_counted_and_others_still_apply()
    {
        var events = new[]
        {
            new HistoricEvent("DepartmentCreated", "not json", D(1, 1), 1),
            new HistoricEvent("DepartmentCreated", """{"DepartmentId":"x"}""", D(1, 1), 2),
            E("DepartmentCreated", D(1, 2), new { DepartmentId = Eng, Name = "Engineering", Identifier = "eng", ParentDepartmentId = (Guid?)null }),
        };

        var snapshot = OrgReplay.At(D(1, 3), events);

        Assert.Equal(2, snapshot.SkippedEvents);
        Assert.Equal(["Engineering"], snapshot.Departments.Select(d => d.Name).ToArray());
    }

    [Fact]
    public void Event_types_the_fold_does_not_know_change_nothing()
    {
        var events = new[] { new HistoricEvent("CurrencyGranted", "{}", D(1, 1), 1) };

        var snapshot = OrgReplay.At(D(1, 2), events);

        Assert.Empty(snapshot.Departments);
        Assert.Equal(0, snapshot.SkippedEvents);
    }

    [Fact]
    public void Property_names_are_read_regardless_of_case()
    {
        var events = new[]
        {
            new HistoricEvent(
                "DepartmentCreated",
                $$"""{"departmentId":"{{Eng}}","name":"Engineering","identifier":"eng","parentDepartmentId":null}""",
                D(1, 1),
                1),
        };

        Assert.Equal(["Engineering"], OrgReplay.At(D(1, 2), events).Departments.Select(d => d.Name).ToArray());
    }

    [Fact]
    public void The_snapshot_never_contains_a_contact_detail()
    {
        var json = JsonSerializer.Serialize(OrgReplay.At(D(12, 31), History()));
        var atHire = JsonSerializer.Serialize(OrgReplay.At(D(1, 20), History()));

        Assert.DoesNotContain("secret.example", json);
        Assert.DoesNotContain("secret.example", atHire);
        Assert.DoesNotContain("@", atHire);
    }
}
