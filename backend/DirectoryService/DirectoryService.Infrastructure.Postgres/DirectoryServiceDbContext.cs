using DirectoryService.Application.Database;
using DirectoryService.Domain.DepartmentLocations;
using DirectoryService.Domain.DepartmentPositions;
using DirectoryService.Domain.Departments;
using DirectoryService.Domain.Locations;
using DirectoryService.Domain.Positions;
using DirectoryService.Infrastructure.Postgres.Embeddings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pgvector.EntityFrameworkCore;

namespace DirectoryService.Infrastructure.Postgres;

public class DirectoryServiceDbContext : DbContext, IReadDbContext
{
    private readonly string _connectionString = null!;
    private readonly ILoggerFactory? _loggerFactory;

    /// <param name="loggerFactory">
    /// Берётся из DI и обязан быть одним и тем же экземпляром для всех контекстов.
    /// EF кэширует свой внутренний ServiceProvider по отпечатку опций, куда входит
    /// ссылка на фабрику логгеров: новая ссылка на каждый контекст означала новый
    /// ServiceProvider, то есть холодный кэш скомпилированных запросов и модели.
    /// null допустим для тестов и design-time - тогда логирование EF просто не настраивается.
    /// </param>
    public DirectoryServiceDbContext(string connectionString, ILoggerFactory? loggerFactory = null)
    {
        _connectionString = connectionString;
        _loggerFactory = loggerFactory;
    }

    public DirectoryServiceDbContext(DbContextOptions<DirectoryServiceDbContext> options)
        : base(options)
    {
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseNpgsql(_connectionString, o => o.UseVector());

        if (_loggerFactory is not null)
        {
            optionsBuilder.UseLoggerFactory(_loggerFactory);
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("directory");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(DirectoryServiceDbContext).Assembly);
    }

    public DbSet<Location> Locations => Set<Location>();

    public DbSet<Position> Positions => Set<Position>();

    public DbSet<Department> Departments => Set<Department>();

    public DbSet<DepartmentLocation> DepartmentLocations => Set<DepartmentLocation>();

    public DbSet<DepartmentPosition> DepartmentPositions => Set<DepartmentPosition>();

    public DbSet<DepartmentEmbedding> DepartmentEmbeddings => Set<DepartmentEmbedding>();

    public IQueryable<Department> DepartmentsRead => Set<Department>().AsNoTracking();

    public IQueryable<Location> LocationsRead => Set<Location>().AsNoTracking();

    public IQueryable<Position> PositionsRead => Set<Position>().AsNoTracking();

    public IQueryable<DepartmentLocation> DepartmentsLocationsRead => Set<DepartmentLocation>().AsNoTracking();

    public IQueryable<DepartmentPosition> DepartmentsPositionsRead => Set<DepartmentPosition>().AsNoTracking();
}