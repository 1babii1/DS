namespace DirectoryService.Contracts.Response.Department;

/// <summary>A department and its active descendants, flat, shallowest first.</summary>
/// <param name="Nodes">At most <c>GetSubtreeHandler.MaxNodes</c> nodes.</param>
/// <param name="Truncated">True when the subtree had more nodes than were returned.</param>
public record DepartmentSubtreeDto(List<ReadDepartmentHierarchyDto> Nodes, bool Truncated);
