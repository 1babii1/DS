using CSharpFunctionalExtensions;
using Dapper;
using DirectoryService.Application.Database;
using DirectoryService.Domain.Departments.ValueObjects;
using DirectoryService.Domain.Positions;
using Microsoft.Extensions.Logging;
using Shared;

namespace DirectoryService.Infrastructure.Postgres.Repositories.Positions;

public class NpgsqlPositionRepository : IPositionRepository
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<NpgsqlPositionRepository> _logger;
    private IPositionRepository _positionRepositoryImplementation;

    public NpgsqlPositionRepository(IDbConnectionFactory connectionFactory, ILogger<NpgsqlPositionRepository> logger, IPositionRepository positionRepositoryImplementation)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
        _positionRepositoryImplementation = positionRepositoryImplementation;
    }

    public async Task<Result<Guid, Error>> Add(Position position, CancellationToken cancellationToken)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);

        var transation = connection.BeginTransaction();

        try
        {
            const string insertPositionSql = """
                                             INSERT INTO positions (id, name, description, is_active, created_at, updated_at)
                                             VALUES (@Id, @Name, @Description, @IsActive, @CreatedAt, @UpdatedAt)
                                             """;

            var insertPositionParams = new
            {
                Id = position.Id.Value,
                Name = position.Name.Value,
                Description = position.Description?.Value,
                IsActive = position.IsActive,
                CreatedAt = position.CreatedAt,
                UpdatedAt = position.UpdatedAt,
            };

            await connection.ExecuteAsync(insertPositionSql, insertPositionParams);

            transation.Commit();

            return position.Id.Value;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error adding position");

            transation.Rollback();

            return Error.Failure("position.insert", "Fail to insert position");
        }
    }

    public Task<Result<IEnumerable<Position>, Error>> GetOrphanPositionByDepartment(
        DepartmentId departmentId,
        CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success<IEnumerable<Position>, Error>(new List<Position>()));
}