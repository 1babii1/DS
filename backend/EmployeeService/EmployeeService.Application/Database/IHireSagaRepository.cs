using EmployeeService.Domain;

namespace EmployeeService.Application.Database;

public interface IHireSagaRepository
{
    Task Add(HireSaga saga, CancellationToken cancellationToken);

    Task<HireSaga?> Get(Guid employeeId, CancellationToken cancellationToken);

    /// <summary>The processes still waiting whose deadline has passed.</summary>
    Task<IReadOnlyList<Guid>> DueEmployees(DateTime now, int take, CancellationToken cancellationToken);
}
