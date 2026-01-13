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

public class GetParentDepartmentsValidator : AbstractValidator<GetParentDepartmentsRequest>
{
    public GetParentDepartmentsValidator()
    {
        RuleFor(x => x.Preferch).NotEmpty().GreaterThan(0).When(x => x.Preferch.HasValue).WithMessage("Preferch cant be null");
        RuleFor(x => x.Page).NotEmpty().GreaterThan(0).When(x => x.Page.HasValue).WithMessage("Page cant be null");
        RuleFor(x => x.Size).NotEmpty().GreaterThan(0)
            .LessThanOrEqualTo(100).When(x => x.Page.HasValue)
            .WithMessage("PageSize cant be null");
    }
}

public class GetParentDepartmentsHandler
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<GetParentDepartmentsHandler> _logger;
    private readonly HybridCache _cache;
    private readonly GetParentDepartmentsValidator _validator;

    public GetParentDepartmentsHandler(
        IDbConnectionFactory connectionFactory,
        ILogger<GetParentDepartmentsHandler> logger, HybridCache cache, GetParentDepartmentsValidator validator)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
        _cache = cache;
        _validator = validator;
    }

    public async Task<List<ReadDepartmentHierarchyDto>> Handle(
        GetParentDepartmentsRequest request,
        CancellationToken cancellationToken)
    {
        // Валидация входных данных
        ValidationResult validateResult = await _validator.ValidateAsync(request, cancellationToken);
        if (!validateResult.IsValid)
        {
            _logger.LogError("Failed to validate departmentId");
            return [];
        }

        var departments = await _cache.GetOrCreateAsync(
            key: GetKey.DepartmentKey.Parents(request.Page, request.Size, request.Preferch),
            factory: async _ => await GetParentDepartmentsFromDb(request, cancellationToken),
            options: new() { LocalCacheExpiration = TimeSpan.FromMinutes(5), Expiration = TimeSpan.FromMinutes(30), },
            cancellationToken: cancellationToken);

        return departments;
    }

    private async Task<List<ReadDepartmentHierarchyDto>> GetParentDepartmentsFromDb(
        GetParentDepartmentsRequest request,
        CancellationToken cancellationToken)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);

        var departments = await connection.QueryAsync<ReadDepartmentHierarchyDto>(
            """
            WITH roots AS (SELECT d.id,
                                  d.name,
                                  d.parent_id,
                                  d.created_at,
                                  d.updated_at,
                                  d.is_active,
                                  d.identifier,
                                  d.path,
                                  d.depth
                           FROM departments d
                           WHERE d.parent_id IS NULL
                           ORDER BY d.created_at
                           OFFSET @offset LIMIT @root_limit)
            SELECT *,
                   (EXISTS (SELECT 1
                            FROM departments d
                            WHERE d.parent_id = roots.id
                            OFFSET @child_limit LIMIT 1)) AS has_more_children
            FROM roots
            UNION ALL
            SELECT c.*, (EXISTS (SELECT 1 FROM departments d WHERE d.parent_id = c.id)) AS has_more_children
            FROM roots r
                     CROSS JOIN LATERAL ( SELECT d.id,
                                                 d.name,
                                                 d.parent_id,
                                                 d.created_at,
                                                 d.updated_at,
                                                 d.is_active,
                                                 d.identifier,
                                                 d.path,
                                                 d.depth
                                          FROM departments d
                                          WHERE d.parent_id = r.id
                                          ORDER BY d.created_at
                                          LIMIT @child_limit
                ) AS c
            """,
            param: new
            {
                offset = (request.Page - 1) * request.Size,
                root_limit = request.Size,
                child_limit = request.Preferch,
            });

        var allDepts = departments.ToList();
        var roots = allDepts.Where(d => d.ParentId == null).ToList();

        foreach (var root in roots)
        {
            root.Children = allDepts
                .Where(d => d.ParentId == root.Id)
                .ToList();
        }

        return roots;
    }
}