using System.Text.Json;
using CSharpFunctionalExtensions;
using Dapper;
using DirectoryService.Application.Database;
using DirectoryService.Contracts.Response.Department;
using DirectoryService.Domain.Departments.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Shared;

namespace DirectoryService.Infrastructure.Postgres.Repositories.Departments;

public class EfCoreDepartmentsRepository : IDepartmentRepository
{
    private readonly DirectoryServiceDbContext _dbContext;
    private readonly ILogger<EfCoreDepartmentsRepository> _logger;

    public EfCoreDepartmentsRepository(
        DirectoryServiceDbContext dbContext,
        ILogger<EfCoreDepartmentsRepository> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<Result<IReadOnlyList<Domain.Departments.Departments>, Error>> GetById(
        IReadOnlyList<DepartmentId> departmentIds,
        CancellationToken cancellationToken)
    {
        try
        {
            var departments = await _dbContext.Departments
                .Where(d => departmentIds.Contains(d.Id))
                .ToListAsync(cancellationToken: cancellationToken);

            if (departments.Count == 0)
            {
                _logger.LogError("Departments not found");
                return Error.NotFound("departments.get", "Departments not found");
            }

            return Result.Success<IReadOnlyList<Domain.Departments.Departments>, Error>(departments);
        }
        catch (NpgsqlException ex)
        {
            _logger.LogError(ex, "Database error getting department by id: {DepartmentId}",
                string.Join(", ", departmentIds.Select(id => id.Value)));
            return Error.Failure("department.get", "Database error");
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error getting departments by ids");

            return Error.Failure("departments.get", "Fail to get departments by ids");
        }
    }

    public async Task<Result<Domain.Departments.Departments, Error>> GetById(
        DepartmentId departmentId,
        CancellationToken cancellationToken)
    {
        try
        {
            var departments = await _dbContext.Departments
                .Where(d => d.Id == departmentId)
                .FirstOrDefaultAsync(cancellationToken: cancellationToken);

            if (departments is null)
            {
                _logger.LogError("Departments not found");
                return Error.NotFound("departments.get", "Departments not found");
            }

            return Result.Success<Domain.Departments.Departments, Error>(departments);
        }
        catch (NpgsqlException ex)
        {
            _logger.LogError(ex, "Database error getting department by id: {DepartmentId}",
                string.Join(", ", departmentId));
            return Error.Failure("department.get", "Database error");
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error getting departments by ids");

            return Error.Failure("departments.get", "Fail to get departments by ids");
        }
    }

    public async Task<Result<Domain.Departments.Departments, Error>> GetByIdIncludeLocations(
        DepartmentId departmentId,
        CancellationToken cancellationToken)
    {
        try
        {
            var department = await _dbContext.Departments
                .Include(d => d.DepartmentsLocationsList)
                .FirstOrDefaultAsync(d => d.Id == departmentId, cancellationToken: cancellationToken);
            if (department is null)
            {
                _logger.LogError("Departments not found");
                return Error.NotFound("department.get", "Departments not found");
            }

            return Result.Success<Domain.Departments.Departments, Error>(department);
        }
        catch (NpgsqlException ex)
        {
            _logger.LogError(ex, "Database error getting department by id: {DepartmentId}", departmentId.Value);
            return Error.Failure("department.get", "Database error");
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error getting department by id");

            return Error.Failure("department.get", "Fail to get department by id");
        }
    }

    public async Task<Result<Domain.Departments.Departments, Error>> GetByIdWithLock(
        DepartmentId departmentId,
        CancellationToken cancellationToken)
    {
        try
        {
            var department = await _dbContext.Departments
                .FromSql($"SELECT * FROM directory.departments WHERE id = {departmentId.Value} FOR UPDATE")
                .FirstOrDefaultAsync(d => d.Id == departmentId, cancellationToken);
            if (department is null)
            {
                _logger.LogError("Departments not found");
                return Error.NotFound("department.get", "Departments not found");
            }

            return Result.Success<Domain.Departments.Departments, Error>(department);
        }
        catch (NpgsqlException ex)
        {
            _logger.LogError(ex, "Database error getting department by id: {DepartmentId}", departmentId.Value);
            return Error.Failure("department.get", "Database error");
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error getting department by id");

            return Error.Failure("department.get", "Fail to get department by id");
        }
    }

    public async Task<UnitResult<Error>> LockChildrenByPath(
        DepartmentPath path,
        CancellationToken cancellationToken = default)
    {
        await _dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT * FROM directory.departments WHERE path <@ {path.Value}::ltree AND path != {path.Value}::ltree FOR UPDATE",
            cancellationToken);

        return UnitResult.Success<Error>();
    }

    public async Task<UnitResult<Error>> UpdateHierarchy(
        DepartmentId newParentId,
        DepartmentPath newParentPath,
        DepartmentId currentId,
        DepartmentPath oldPath,
        CancellationToken cancellationToken)
    {
        try
        {
            // Re-parent first: this matches on the old path, so it has to run before the
            // path rewrite below changes it.
            await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE directory.departments SET parent_id = {newParentId.Value} WHERE path = {oldPath.Value}::ltree",
                cancellationToken);

            // Rewrite the moved department and its whole subtree in one statement. The same
            // expression covers both: for the node itself subpath() yields its own last
            // segment, for a descendant it yields the tail below the old parent. Depth is
            // recomputed from the new path rather than adjusted by a delta - deltas go
            // negative as soon as a node moves up the tree.
            await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 UPDATE directory.departments
                 SET path  = {newParentPath.Value}::ltree || subpath(path, nlevel({oldPath.Value}::ltree) - 1),
                     depth = (nlevel({newParentPath.Value}::ltree || subpath(path, nlevel({oldPath.Value}::ltree) - 1)) - 1)::smallint
                 WHERE path <@ {oldPath.Value}::ltree
                 """,
                cancellationToken);

            return UnitResult.Success<Error>();
        }
        catch (Exception e)
        {
            _logger.LogError(
                e,
                "Failed to move department {DepartmentId} under {NewParentId}",
                currentId.Value,
                newParentId.Value);

            return Error.Failure("department.move", "Failed to move department");
        }
    }

    public async Task<Result<Guid, Error>> Add(
        Domain.Departments.Departments department,
        CancellationToken cancellationToken)
    {
        try
        {
            await _dbContext.Departments.AddAsync(department, cancellationToken);

            await _dbContext.SaveChangesAsync(cancellationToken);

            return department.Id.Value;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error adding department");

            return Error.Failure("department.insert", "Fail to insert department");
        }
    }

    public async Task<Result<IEnumerable<DepartmentId>, Error>> GetDepartmentsIds(
        IEnumerable<DepartmentId> departmentIds,
        CancellationToken cancellationToken)
    {
        try
        {
            var allDepartmentIds = await _dbContext.Departments
                .Select(d => d.Id)
                .ToListAsync(cancellationToken: cancellationToken);

            var missIds = departmentIds.Except(allDepartmentIds);

            return Result.Success<IEnumerable<DepartmentId>, Error>(missIds);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pgEx && pgEx.SqlState == "23505")
        {
            _logger.LogError(ex, "Error getting departments ids");

            return Error.Failure("department.get", "Fail to get departments ids");
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error getting departments ids");

            return Error.Failure("department.get", "Fail to get departments ids");
        }
    }
}