using EmployeeService.Application.Database;
using EmployeeService.Domain;
using Microsoft.EntityFrameworkCore;

namespace EmployeeService.Infrastructure.Postgres;

public class IdempotencyRepository(EmployeeDbContext dbContext) : IIdempotencyRepository
{
    public Task<IdempotencyRecord?> Find(string scope, string key, CancellationToken cancellationToken) =>
        dbContext.IdempotencyRecords.AsNoTracking().SingleOrDefaultAsync(r => r.Scope == scope && r.Key == key, cancellationToken);

    public async Task Add(IdempotencyRecord record, CancellationToken cancellationToken) =>
        await dbContext.IdempotencyRecords.AddAsync(record, cancellationToken);

    public void DiscardPending() => dbContext.ChangeTracker.Clear();
}
