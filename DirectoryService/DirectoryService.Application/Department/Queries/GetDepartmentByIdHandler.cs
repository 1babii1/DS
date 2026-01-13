using DirectoryService.Application.Cache;
using DirectoryService.Application.Database;
using DirectoryService.Contracts.Request.Department;
using DirectoryService.Contracts.Response.Department;
using DirectoryService.Domain.Departments.ValueObjects;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;

namespace DirectoryService.Application.Department.Queries;

public class GetDepartmentByIdValidator : AbstractValidator<GetDepartmentByIdRequest>
{
    public GetDepartmentByIdValidator()
    {
        RuleFor(x => x.DepartmentId).NotNull().NotEmpty().WithMessage("departmentId cant be null");
    }
}

public class GetDepartmentByIdHandler
{
    private readonly IReadDbContext _readDbContext;
    private readonly HybridCache _cache;
    private readonly ILogger<GetDepartmentByIdHandler> _logger;
    private readonly GetDepartmentByIdValidator _validator;

    public GetDepartmentByIdHandler(IReadDbContext readDbContext, HybridCache cache,
        ILogger<GetDepartmentByIdHandler> logger, GetDepartmentByIdValidator validator)
    {
        _readDbContext = readDbContext;
        _cache = cache;
        _logger = logger;
        _validator = validator;
    }

    public async Task<ReadDepartmentWithChildrenDto?> Handle(
        GetDepartmentByIdRequest request,
        CancellationToken cancellationToken)
    {
        // Валидация входных данных
        ValidationResult validateResult = await _validator.ValidateAsync(request, cancellationToken);
        if (!validateResult.IsValid)
        {
            _logger.LogError("Failed to validate departmentId");
            return null;
        }

        var department = await _cache.GetOrCreateAsync(
            key: GetKey.DepartmentKey.ById(DepartmentId.FromValue(request.DepartmentId)),
            factory: async _ => await GetDepartmentByIdFromDb(request, cancellationToken),
            options: new() { LocalCacheExpiration = TimeSpan.FromMinutes(5), Expiration = TimeSpan.FromMinutes(30), },
            cancellationToken: cancellationToken);

        return department;
    }

    private async Task<ReadDepartmentWithChildrenDto?> GetDepartmentByIdFromDb(
        GetDepartmentByIdRequest request,
        CancellationToken cancellationToken)
    {
        var department = await _readDbContext.DepartmentsRead
            .Include(d => d.DepartmentsChildrenList)
            .Where(d => d.Id == DepartmentId.FromValue(request.DepartmentId))
            .Select(d => new ReadDepartmentWithChildrenDto
            {
                Id = d.Id.Value,
                ParentId = d.ParentId!.Value,
                Name = d.Name.Value,
                Identifier = d.Identifier.Value,
                Path = d.Path.Value,
                Depth = d.Depth,
                IsActive = d.IsActive,
                CreatedAt = d.CreatedAt,
                UpdatedAt = d.UpdatedAt,
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (department is null)
        {
            _logger.LogError("department not found");
            return null;
        }

        _logger.LogInformation("department found");
        return department;
    }
}