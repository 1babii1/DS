using DirectoryService.Domain.DepartmentLocations;
using DirectoryService.Domain.Locations.ValueObjects;
using Shared;

namespace DirectoryService.Domain.Locations;

public class Locations : ISoftDeletable
{
    public LocationId Id { get; private set; } = null!;

    public LocationName Name { get; private set; } = null!;

    public Timezone Timezone { get; private set; } = null!;

    public Address Address { get; private set; } = null!;

    public bool IsActive { get; private set; }

    public DateTime CreatedAt { get; private set; }

    public DateTime UpdatedAt { get; private set; }

    public DateTime? DeletedAt { get; set; }

    public IReadOnlyList<DepartmentLocation> DepartmentLocationsList { get; private set; } = null!;

    // EF Core
    public Locations() { }

    public Locations(LocationId id, LocationName name, Timezone timezone, Address address,
        IReadOnlyList<DepartmentLocation> departmentLocationsList)
    {
        Id = id;
        Name = name;
        Timezone = timezone;
        Address = address;
        IsActive = true;
        CreatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
        DepartmentLocationsList = departmentLocationsList.ToList();
    }

    // public static Result<Locations> Create(
    //     LocationId id,
    //     LocationName name,
    //     Timezone timezone,
    //     Address address,
    //     IReadOnlyList<DepartmentLocation> departmentLocationsList)
    // {
    //     Locations locations = new(id, name, timezone, address, departmentLocationsList);
    //
    //     return Result.Success(locations);
    // }
    public void SetId(LocationId id) => Id = id;

    public void SetName(LocationName name) => Name = name;

    public void SetTimezone(Timezone timezone) => Timezone = timezone;

    public void SetAddress(Address address) => Address = address;

    public void SetIsActive(bool isActive) => IsActive = isActive;

    public void SetCreatedAt(DateTime createdAt) => CreatedAt = createdAt;

    public void SetUpdatedAt(DateTime updatedAt) => UpdatedAt = updatedAt;

    public void SetDepartmentLocationsList(IReadOnlyList<DepartmentLocation> departmentLocationsList) =>
        DepartmentLocationsList = departmentLocationsList;

    public void Delete()
    {
        IsActive = false;
        DeletedAt = DateTime.UtcNow;
    }

    public void Activate() => IsActive = true;
}