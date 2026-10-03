using EmployeeService.Application.Database;
using EmployeeService.Domain;
using Microsoft.EntityFrameworkCore;

namespace EmployeeService.Infrastructure.Postgres;

public class HireSagaRepository(EmployeeDbContext dbContext) : IHireSagaRepository
{
    public async Task Add(HireSaga saga, CancellationToken cancellationToken) =>
        await dbContext.HireSagas.AddAsync(saga, cancellationToken);

    public Task<HireSaga?> Get(Guid employeeId, CancellationToken cancellationToken) =>
        dbContext.HireSagas.SingleOrDefaultAsync(s => s.EmployeeId == employeeId, cancellationToken);

    public async Task<IReadOnlyList<Guid>> DueEmployees(DateTime now, int take, CancellationToken cancellationToken) =>
        await dbContext.HireSagas.AsNoTracking()
            .Where(s => s.State == HireSagaState.Started && s.Deadline <= now)
            .OrderBy(s => s.Deadline)
            .Select(s => s.EmployeeId)
            .Take(take)
            .ToListAsync(cancellationToken);
}
