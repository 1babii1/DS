using Dapper;
using DirectoryService.Application.Cache;
using DirectoryService.Application.Database;
using DirectoryService.Contracts.Request.Location;
using DirectoryService.Contracts.Response.Location;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;

namespace DirectoryService.Application.Location.Queries;

public class GetLocationByDepartmentValidation : AbstractValidator<GetLocationByDepartmentRequest>
{
    public GetLocationByDepartmentValidation()
    {
        RuleFor(x => x.Page).GreaterThan(0);
        RuleFor(x => x.PageSize).GreaterThan(0)
            .LessThanOrEqualTo(100);
        RuleFor(x => x.Search).MaximumLength(150);
        RuleFor(x => x.SortBy)
           .Must(sortBy => new[] { "name", "city", "created_at", "updated_at" }.Contains(sortBy?.ToLower()))
           .WithMessage("Invalid sort field. Use: name, city, created_at, updated_at");
        RuleFor(x => x.SortDirection)
           .Must(direction => new[] { "ASC", "DESC" }.Contains(direction?.ToUpper()))
           .WithMessage("Invalid sort direction. Use: ASC, DESC");
    }
}

public class GetLocationByDepartmentHandle
{
    private readonly IDbConnectionFactory _dbConnectionFactory;
    private readonly ILogger<GetLocationByDepartmentHandle> _logger;
    private readonly GetLocationByDepartmentValidation _validator;
    private readonly HybridCache _cache;

    public GetLocationByDepartmentHandle(IDbConnectionFactory connectionFactory, GetLocationByDepartmentValidation validator,
        HybridCache cache, ILogger<GetLocationByDepartmentHandle> logger)
    {
        _dbConnectionFactory = connectionFactory;
        _validator = validator;
        _cache = cache;
        _logger = logger;
    }

    public async Task<List<ReadLocationDto>?> Handle(
           GetLocationByDepartmentRequest request,
           CancellationToken cancellationToken)
    {
        // Валидация входных данных
        ValidationResult validateResult = await _validator.ValidateAsync(request, cancellationToken);
        if (!validateResult.IsValid)
        {
            _logger.LogError("Failed to validate location search request");
            return [];
        }

        _logger.LogInformation("Searching locations with search: {Search}", request.Search);

        var locations = await _cache.GetOrCreateAsync(
            key: GetKey.LocationKey.ByFilters(request.Search, request.IsActive, request.Page, request.PageSize, request.SortBy, request.SortDirection),
            factory: async _ => await GetLocations(request, cancellationToken),
            options: new() { LocalCacheExpiration = TimeSpan.FromMinutes(5), Expiration = TimeSpan.FromMinutes(30), },
            cancellationToken: cancellationToken);

        return locations;
    }

    public async Task<List<ReadLocationDto>?> GetLocations(
        GetLocationByDepartmentRequest request,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Fetching locations from database with filters");
        var connection = await _dbConnectionFactory.CreateConnectionAsync(cancellationToken);
        var parameters = new DynamicParameters();
        var conditions = new List<string>();

        // ✅ ИЗМЕНЕНИЕ: основной источник = locations!
        var fromClause = "FROM locations l";

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            conditions.Add("l.name ILIKE '%' || @search || '%'");
            parameters.Add("search", request.Search);
        }

        if (request.IsActive.HasValue)
        {
            conditions.Add("l.is_active = @isActive");
            parameters.Add("isActive", request.IsActive.Value);
        }

        // ✅ EXISTS вместо JOIN!
        if (request.DepartmentId != null)
        {
            conditions.Add("""
            EXISTS (
                SELECT 1 FROM department_locations dl
                WHERE dl.location_id = l.id
                AND dl.department_id = ANY(@DepartmentId::uuid[])
            )
            """);
            parameters.Add("DepartmentId", request.DepartmentId);
        }

        parameters.Add("limit", request.PageSize);
        parameters.Add("offset", (request.Page - 1) * request.PageSize);

        var whereClause = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : string.Empty;

        var sortField = request.SortBy?.ToLower() switch
        {
            "name" => "l.name",
            "city" => "l.city",
            "created_at" => "l.created_at",
            "updated_at" => "l.updated_at",
            _ => "l.created_at"
        };

        var sortDirection = request.SortDirection?.ToUpper() switch
        {
            "DESC" => "DESC",
            _ => "ASC"
        };

        return (await connection.QueryAsync<ReadLocationDto>(
       $"""
        SELECT l.id, l.name, l.timezone, l.street, l.city, l.country,
               l.is_active, l.created_at, l.updated_at
        {fromClause}
        {whereClause}
        ORDER BY {sortField} {sortDirection}
        LIMIT @limit OFFSET @offset
        """, parameters)).ToList();
    }
}