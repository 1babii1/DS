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

public class GetLocationBySearchValidation : AbstractValidator<GetLocationBySearchRequest>
{
    public GetLocationBySearchValidation()
    {
        RuleFor(x => x.Page).NotEmpty().GreaterThan(0).When(x => x.Page.HasValue).WithMessage("Page cant be null");
        RuleFor(x => x.Size).NotEmpty().GreaterThan(0)
            .LessThanOrEqualTo(100).When(x => x.Page.HasValue)
            .WithMessage("PageSize cant be null");
        RuleFor(x => x.Search).NotEmpty().NotNull().MaximumLength(150).WithMessage("Search cant be null");
    }
}

public class GetLocationBySearchHandler
{
    private readonly GetLocationBySearchValidation _validator;
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly HybridCache _cache;
    private readonly ILogger<GetLocationBySearchHandler> _logger;

    public GetLocationBySearchHandler(GetLocationBySearchValidation validator, IDbConnectionFactory connectionFactory,
        HybridCache cache, ILogger<GetLocationBySearchHandler> logger)
    {
        _validator = validator;
        _connectionFactory = connectionFactory;
        _cache = cache;
        _logger = logger;
    }

    public async Task<List<ReadLocationDto>> Handle(GetLocationBySearchRequest bySearchRequest, CancellationToken cancellationToken)
    {
        // Валидация входных данных
        ValidationResult validateResult = await _validator.ValidateAsync(bySearchRequest, cancellationToken);
        if (!validateResult.IsValid)
        {
            _logger.LogError("Failed to validate location search request");
            return [];
        }

        _logger.LogInformation("Searching locations with search: {Search}", bySearchRequest.Search);

        var locations = await _cache.GetOrCreateAsync(
            key: GetKey.LocationKey.BySearch(bySearchRequest.Search),
            factory: async _ => await GetLocations(bySearchRequest, cancellationToken),
            options: new() { LocalCacheExpiration = TimeSpan.FromMinutes(5), Expiration = TimeSpan.FromMinutes(30), },
            cancellationToken: cancellationToken);

        return locations;
    }

    public async Task<List<ReadLocationDto>> GetLocations(
        GetLocationBySearchRequest bySearchRequest,
        CancellationToken cancellationToken)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);

        var locations = await connection.QueryAsync<ReadLocationDto>(
            """
            SELECT l.id,
                   l.name,
                   l.timezone,
                   l.city,
                   l.country,
                   l.street,
                   l.created_at,
                   l.updated_at
            FROM locations l
            WHERE l.name ILIKE '%' || @search || '%' 
            ORDER BY l.created_at
            LIMIT @pageSize OFFSET @offset
            """,
            param: new
            {
                search = bySearchRequest.Search, pageSize = bySearchRequest.Size, offset = (bySearchRequest.Page - 1) * bySearchRequest.Size,
            });

        return locations.ToList();
    }
}