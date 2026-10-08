namespace EmployeeService.Application.Authorization;

/// <summary>
/// Keeps the store's picture of the department tree in step with DirectoryService's, from its events (ADR 0057). Every method is idempotent and
/// every one is a single atomic write, because the events arrive at least once and a move must never leave a department with two parents, or none,
/// for the moment between a delete and a write.
/// </summary>
public sealed class DepartmentTreeSync(IFgaClient store)
{
    private const string Parent = "parent";

    public Task OnCreated(Guid departmentId, Guid? parentId, CancellationToken cancellationToken) =>
        parentId is { } parent
            ? store.WriteAsync([ParentTuple(departmentId, parent)], [], cancellationToken)
            : Task.CompletedTask;

    /// <summary>
    /// The department now has this parent. What it had is read from the store rather than taken from the event: the event's "old parent" is
    /// what Directory believed, and the store may hold something else if an earlier event was missed or arrived twice.
    /// </summary>
    public async Task OnMoved(Guid departmentId, Guid newParentId, CancellationToken cancellationToken)
    {
        var current = await store.ReadAsync(FgaTuple.DepartmentId(departmentId), Parent, cancellationToken);
        var wanted = ParentTuple(departmentId, newParentId);
        await store.WriteAsync([wanted], current.Where(t => t != wanted).ToList(), cancellationToken);
    }

    /// <summary>What was said about the department as an object goes: its parent and its managers. Departments below it are not touched; Directory does not delete a department that has any.</summary>
    public async Task OnDeleted(Guid departmentId, CancellationToken cancellationToken)
    {
        var tuples = await store.ReadAsync(FgaTuple.DepartmentId(departmentId), relation: null, cancellationToken);
        if (tuples.Count > 0)
        {
            await store.WriteAsync([], tuples, cancellationToken);
        }
    }

    private static FgaTuple ParentTuple(Guid departmentId, Guid parentId) =>
        new(FgaTuple.DepartmentId(parentId), Parent, FgaTuple.DepartmentId(departmentId));
}
