using Dapper;
using DirectoryService.Application.Cache;
using DirectoryService.Application.Database;
using DirectoryService.Contracts.Request.Department;
using DirectoryService.Contracts.Response.Department;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;

namespace DirectoryService.Application.Department.Queries;

public class GetChildrenLazyValidator : AbstractValidator<GetChildrenLazyCommand>
{
    public GetChildrenLazyValidator()
    {
        RuleFor(x => x.ParentId).NotNull().NotEmpty().WithMessage("ParentId cant be null");
        RuleFor(x => x.Request.Page).GreaterThan(0).WithMessage("Page cant be null");
        RuleFor(x => x.Request.PageSize).GreaterThan(0)
            .LessThanOrEqualTo(100)
            .WithMessage("PageSize cant be null");
    }
}

public record PoginationResponse<T>(List<T> Items, int TotalCount, bool NextPageExists, int? Page);

public record GetChildrenLazyCommand([FromRoute] Guid ParentId, [FromQuery] GetChildrenLazyRequest Request);

public class GetChildrenLazyHandler
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly GetChildrenLazyValidator _validator;
    private readonly ILogger<GetChildrenLazyHandler> _logger;
    private readonly HybridCache _cache;

    public GetChildrenLazyHandler(IDbConnectionFactory connectionFactory, GetChildrenLazyValidator validator,
        ILogger<GetChildrenLazyHandler> logger, HybridCache cache)
    {
        _connectionFactory = connectionFactory;
        _validator = validator;
        _logger = logger;
        _cache = cache;
    }

    public async Task<PoginationResponse<ReadDepartmentHierarchyDto>?> Handle(
        GetChildrenLazyCommand request,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation($"{request.ParentId} - Get children lazy departments");
        _logger.LogInformation($"{request.Request.Page} - {request.Request.PageSize}");

        // Валидация входных данных
        ValidationResult validateResult = await _validator.ValidateAsync(request, cancellationToken);
        if (!validateResult.IsValid)
        {
            _logger.LogError("Failed to validate departmentId");
            return null;
        }

        var departments = await _cache.GetOrCreateAsync(
            key: GetKey.DepartmentKey.Children(request.ParentId, request.Request.Page, request.Request.PageSize),
            factory: async _ => await GetChildrenLazyFromDb(request, cancellationToken),
            options: new() { LocalCacheExpiration = TimeSpan.FromMinutes(5), Expiration = TimeSpan.FromMinutes(30), },
            cancellationToken: cancellationToken);

        return departments;
    }

    private async Task<PoginationResponse<ReadDepartmentHierarchyDto>> GetChildrenLazyFromDb(
        GetChildrenLazyCommand request,
        CancellationToken cancellationToken)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);

        var departments = await connection.QueryMultipleAsync(
            """
            SELECT COUNT(*) as total_count
                FROM departments
                WHERE parent_id = @departmentId;

            SELECT d.id,
                   d.name,
                   d.parent_id,
                   d.created_at,
                   d.updated_at,
                   d.is_active,
                   d.identifier,
                   d.path,
                   d.depth,
                   (EXISTS (SELECT 1 FROM departments WHERE parent_id = d.id)) AS has_more_children
            FROM departments d
            WHERE d.parent_id = @departmentId
            ORDER BY d.created_at
            LIMIT @pageSize OFFSET @offset
            """,
            param:
        new
        {
            departmentId = request.ParentId,
            pageSize = request.Request.PageSize,
            offset = (request.Request.Page - 1) * request.Request.PageSize,
        });

        var total_count = await departments.ReadSingleAsync<int>();
        var items = (await departments.ReadAsync<ReadDepartmentHierarchyDto>()).ToList();

        return new PoginationResponse<ReadDepartmentHierarchyDto>(items, total_count, (request.Request.Page * request.Request.PageSize) < total_count, request.Request.Page);
    }
}