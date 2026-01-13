using System.Text.Json.Serialization;
using CSharpFunctionalExtensions;
using DirectoryService.Domain.DepartmentLocations.ValueObjects;
using DirectoryService.Domain.Departments.ValueObjects;
using DirectoryService.Domain.Locations.ValueObjects;

namespace DirectoryService.Domain.DepartmentLocations;

public class DepartmentLocation
{
    public DepartmentLocationsId Id { get; private set; }

    public DepartmentId DepartmentId { get; private set; }

    public LocationId LocationId { get; private set; }

    [JsonConstructor]
    private DepartmentLocation(DepartmentLocationsId id, DepartmentId departmentId, LocationId locationId)
    {
        Id = id;
        DepartmentId = departmentId;
        LocationId = locationId;
    }

    public static Result<DepartmentLocation> Create(DepartmentLocationsId? id, DepartmentId departmentId,
        LocationId locationId)
    {
        DepartmentLocation departmentLocation = new(id ?? DepartmentLocationsId.NewDepartmentLocationsId(), departmentId, locationId);

        return Result.Success(departmentLocation);
    }

    public void SetId(DepartmentLocationsId id) => Id = id;

    public void SetDepartmentId(DepartmentId departmentId) => DepartmentId = departmentId;

    public void SetLocationId(LocationId locationId) => LocationId = locationId;
}