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
        RuleFor(x => x.Request.Page).NotEmpty().GreaterThan(0).When(x => x.Request.Page.HasValue).WithMessage("Page cant be null");
        RuleFor(x => x.Request.PageSize).NotEmpty().GreaterThan(0)
            .LessThanOrEqualTo(100).When(x => x.Request.Page.HasValue)
            .WithMessage("PageSize cant be null");
    }
}

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

    public async Task<List<ReadDepartmentHierarchyDto>?> Handle(
        GetChildrenLazyCommand request,
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
            key: GetKey.DepartmentKey.Children(request.ParentId),
            factory: async _ => await GetChildrenLazyFromDb(request, cancellationToken),
            options: new() { LocalCacheExpiration = TimeSpan.FromMinutes(5), Expiration = TimeSpan.FromMinutes(30), },
            cancellationToken: cancellationToken);

        return departments;
    }

    private async Task<List<ReadDepartmentHierarchyDto>> GetChildrenLazyFromDb(
        GetChildrenLazyCommand request,
        CancellationToken cancellationToken)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);

        var departments = await connection.QueryAsync<ReadDepartmentHierarchyDto>(
            """
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
            param: new
            {
                departmentId = request.ParentId,
                pageSize = request.Request.PageSize,
                offset = (request.Request.Page - 1) * request.Request.PageSize,
            });

        return departments.ToList();
    }
}