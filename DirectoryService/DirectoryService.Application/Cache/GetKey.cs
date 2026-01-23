using DirectoryService.Domain.Departments.ValueObjects;

namespace DirectoryService.Application.Cache;

public static class GetKey
{
    public static class DepartmentKey
    {
        public static string BySearch(string search) => $"departmentBySearch:{search}";

        public static string ById(DepartmentId departmentId) => $"department:{departmentId}";

        public static List<string> ById(Guid[] departmentId) => departmentId.Select(id => $"department:{id}").ToList();

        public static string Children(Guid parentId, int? page, int? size) => $"departmentChildren:{parentId}:{page}:{size}";

        public static string ByLocation(Guid[]? locationIds = null, string? search = null)
        {
            string partLocation =
                string.Join(
                    ",",
                    locationIds != null ? locationIds.OrderBy(x => x) : new[] { string.Empty });
            return $"departmentByLocation:{partLocation}{(search != null ? $"|{search}" : string.Empty)}";
        }

        public static string TopByPositions() => "DepartmentsTopByPositions";

        public static string Parents(int? page, int? size, int? prefetch)
            => $"departmentsWithChildren:{page}|{size}|{prefetch}";
    }

    public static class LocationKey
    {
        public static string BySearch(string search) => $"locationBySearch:{search}";

        public static string ById(Guid locationId) => $"location:{locationId}";

        public static string ByDepartment(Guid departmentId) => $"locationByDepartment:{departmentId}";

        public static string ByFilters(string? search, bool? isActive, int? page, int? size, string? sortBy, string? sortDirection)
            => $"locationByFilters:{search}|{isActive}|{page}|{size}|{sortBy}|{sortDirection}";
    }

    public static class PositionKey
    {
    }
}