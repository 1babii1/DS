using DirectoryService.Application.Cache;
using DirectoryService.Application.Database;
using DirectoryService.Contracts.Request.Department;
using DirectoryService.Contracts.Response.Department;
using DirectoryService.Domain.Locations.ValueObjects;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;

namespace DirectoryService.Application.Department.Queries;

public class GetDepartmentByLocationValidator : AbstractValidator<GetDepartmentByLocationRequest>
{
    public GetDepartmentByLocationValidator()
    {
        RuleFor(x => x.Search).MaximumLength(150)
            .When(x => !string.IsNullOrWhiteSpace(x.Search));
        RuleFor(x => x.LocationIds).NotNull()
            .WithMessage("LocationIds required for filtering");
        RuleForEach(x => x.LocationIds).NotEmpty()
            .WithMessage("LocationId cannot be empty");
        RuleFor(x => x.Page).NotEmpty().GreaterThan(0).When(x => x.Page.HasValue).WithMessage("Page cant be null");
        RuleFor(x => x.PageSize).NotEmpty().GreaterThan(0)
            .LessThanOrEqualTo(100).When(x => x.Page.HasValue)
            .WithMessage("PageSize cant be null");
    }
}

public class GetDepartmentByLocationHandler
{
    private readonly IReadDbContext _readDbContext;
    private readonly ILogger<GetDepartmentByLocationHandler> _logger;
    private readonly HybridCache _cache;
    private readonly GetDepartmentByLocationValidator _validator;

    public GetDepartmentByLocationHandler(IReadDbContext readDbContext, ILogger<GetDepartmentByLocationHandler> logger,
        HybridCache cache, GetDepartmentByLocationValidator validator)
    {
        _readDbContext = readDbContext;
        _logger = logger;
        _cache = cache;
        _validator = validator;
    }

    public async Task<List<ReadDepartmentDto>?> Handle(
        GetDepartmentByLocationRequest request,
        CancellationToken cancellationToken)
    {
        // Валидация входных данных
        ValidationResult validateResult = await _validator.ValidateAsync(request, cancellationToken);
        if (!validateResult.IsValid)
        {
            _logger.LogError("Failed to validate departmentId");
            return [];
        }

        // Получение из кэша
        var departments = await _cache.GetOrCreateAsync(
            key: GetKey.DepartmentKey.ByLocation(request.LocationIds, request.Search),
            factory: async _ => await GetDepartmentByLocationFromDb(request, cancellationToken),
            options: new() { LocalCacheExpiration = TimeSpan.FromMinutes(5), Expiration = TimeSpan.FromMinutes(30), },
            cancellationToken: cancellationToken);

        if (departments == null)
        {
            _logger.LogError("failed to get department");
            return [];
        }

        return departments;
    }

    private async Task<List<ReadDepartmentDto>?> GetDepartmentByLocationFromDb(
        GetDepartmentByLocationRequest request,
        CancellationToken cancellationToken)
    {
        var department = _readDbContext.DepartmentsRead;

        if (request.LocationIds != null)
        {
            var locationIds = request.LocationIds.Select(LocationId.FromValue).ToList();
            department = department.Where(d =>
                d.DepartmentsLocationsList.Any(dl => locationIds.Contains(dl.LocationId)));
        }

        if (!string.IsNullOrWhiteSpace(request.Search) && request.Search != null)
        {
            department = department.Where(d => d.Name.Value.Contains(request.Search));
        }

        int page = request.Page ?? 1;
        int pageSize = request.PageSize ?? 10;

        int skipCount = (page - 1) * pageSize;

        var departmentDto = await department
            .OrderBy(d => d.Name.Value)
            .Skip(skipCount)
            .Take(pageSize)
            .Select(d => new ReadDepartmentDto
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
            .ToListAsync(cancellationToken);

        _logger.LogInformation("Found {Count} departments", departmentDto.Count);

        return departmentDto;
    }
}