using Dapper;
using DirectoryService.Application.Cache;
using DirectoryService.Application.Database;
using DirectoryService.Contracts.Request.Department;
using DirectoryService.Contracts.Response.Department;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;

namespace DirectoryService.Application.Department.Queries;

public class GetDepartmentsBySearchValidation : AbstractValidator<GetDepartmentsBySearchRequest>
{
    public GetDepartmentsBySearchValidation()
    {
        RuleFor(x => x.Page).NotEmpty().GreaterThan(0).When(x => x.Page.HasValue).WithMessage("Page cant be null");
        RuleFor(x => x.Size).NotEmpty().GreaterThan(0)
            .LessThanOrEqualTo(100).When(x => x.Page.HasValue)
            .WithMessage("PageSize cant be null");
        RuleFor(x => x.Search).NotEmpty().NotNull().MaximumLength(150).WithMessage("Search cant be null");
    }
}

public class GetDepartmentsWithFiltersHandler
{
    private readonly GetDepartmentsBySearchValidation _validator;
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly HybridCache _cache;
    private readonly ILogger<GetDepartmentsWithFiltersHandler> _logger;

    public GetDepartmentsWithFiltersHandler(GetDepartmentsBySearchValidation validator, IDbConnectionFactory connectionFactory,
        HybridCache cache, ILogger<GetDepartmentsWithFiltersHandler> logger)
    {
        _validator = validator;
        _connectionFactory = connectionFactory;
        _cache = cache;
        _logger = logger;
    }

    public async Task<List<DepartmentBySearch>> Handle(GetDepartmentsBySearchRequest bySearchRequest, CancellationToken cancellationToken)
    {
        // Валидация входных данных
        ValidationResult validateResult = await _validator.ValidateAsync(bySearchRequest, cancellationToken);
        if (!validateResult.IsValid)
        {
            _logger.LogError("Failed to validate departmentId");
            return [];
        }

        _logger.LogInformation("Searching departments with search: {Search}", bySearchRequest.Search);

        var departments = await _cache.GetOrCreateAsync(
            key: GetKey.DepartmentKey.BySearch(bySearchRequest.Search),
            factory: async _ => await GetDepartments(bySearchRequest, cancellationToken),
            options: new() { LocalCacheExpiration = TimeSpan.FromMinutes(5), Expiration = TimeSpan.FromMinutes(30), },
            cancellationToken: cancellationToken);

        return departments;
    }

    public async Task<List<DepartmentBySearch>> GetDepartments(
        GetDepartmentsBySearchRequest bySearchRequest,
        CancellationToken cancellationToken)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);

        var departments = await connection.QueryAsync<DepartmentBySearch>(
            """
            SELECT d.id,
                   d.name,
                   d.parent_id,
                   d.created_at,
                   d.updated_at,
                   d.is_active,
                   d.identifier,
                   d.path,
                   d.depth
            FROM departments d
            WHERE d.name ILIKE '%' || @search || '%'
            OR d.identifier ILIKE '%' || @search || '%'
            ORDER BY d.created_at
            LIMIT @pageSize OFFSET @offset
            """,
            param: new
            {
                search = bySearchRequest.Search,
                pageSize = bySearchRequest.Size,
                offset = (bySearchRequest.Page - 1) * bySearchRequest.Size,
            });

        return departments.ToList();
    }
}