using DirectoryService.Application.Database;
using DirectoryService.Domain.DepartmentLocations;
using DirectoryService.Domain.DepartmentPositions;
using DirectoryService.Domain.Departments;
using DirectoryService.Domain.Locations;
using DirectoryService.Domain.Positions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DirectoryService.Infrastructure.Postgres;

public class DirectoryServiceDbContext : DbContext, IReadDbContext
{
    private readonly string _connectionString = null!;

    public DirectoryServiceDbContext(string connectionString)
    {
        _connectionString = connectionString;
    }

    public DirectoryServiceDbContext(DbContextOptions<DirectoryServiceDbContext> options)
        : base(options)
    {
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseNpgsql(_connectionString);
        optionsBuilder.UseLoggerFactory(LoggerFactory);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(DirectoryServiceDbContext).Assembly);
    }

    public DbSet<Locations> Locations => Set<Locations>();

    public DbSet<Position> Positions => Set<Position>();

    public DbSet<Departments> Departments => Set<Departments>();

    public DbSet<DepartmentLocation> DepartmentLocations => Set<DepartmentLocation>();

    public DbSet<DepartmentPosition> DepartmentPositions => Set<DepartmentPosition>();

    public IQueryable<Departments> DepartmentsRead => Set<Departments>().AsNoTracking();

    public IQueryable<Locations> LocationsRead => Set<Locations>().AsNoTracking();

    public IQueryable<Position> PositionsRead => Set<Position>().AsNoTracking();

    public IQueryable<DepartmentLocation> DepartmentsLocationsRead => Set<DepartmentLocation>().AsNoTracking();

    public IQueryable<DepartmentPosition> DepartmentsPositionsRead => Set<DepartmentPosition>().AsNoTracking();

    private ILoggerFactory LoggerFactory =>
        Microsoft.Extensions.Logging.LoggerFactory.Create(builder => builder.AddConsole());
}