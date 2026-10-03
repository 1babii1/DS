using EmployeeService.Domain;

namespace EmployeeService.Application.Database;

public interface IIdempotencyRepository
{
    Task<IdempotencyRecord?> Find(string scope, string key, CancellationToken cancellationToken);

    Task Add(IdempotencyRecord record, CancellationToken cancellationToken);

    /// <summary>Forgets what this unit of work read and staged, so that a lost race can be re-read cleanly.</summary>
    void DiscardPending();
}
