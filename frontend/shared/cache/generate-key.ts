export const generateKeyTSQueryDepartment = {
	All: () => 'departments',
	Roots: () => 'departmentsRoots',
	ById: (id: string) => `department:${id}`,
	BySearch: (search: string) => `departmentBySearch:${search}`,
	Children: (parentId: string, page?: number, size?: number) =>
		`departmentChildren:${parentId}:page:${page}:size:${size}`,
	ByLocation: (locationIds?: string[]) =>
		`departmentByLocation:${locationIds?.join(',')}`,
	TopByPositions: () => 'DepartmentsTopByPositions'
}

export const generateKeyTSQueryLocation = {
	All: () => 'locations',
	ById: (id: string) => `location:${id}`,
	ByFilters: (
		search: string,
		sortBy: string,
		pageSize: string,
		isActive: string,
		departmentId: string[],
		sortDirection: string
	) =>
		`locationByFilters:${search}:${sortBy}:${pageSize}:${isActive || ''}:${departmentId}:${sortDirection || ''}`
}

export const generateKeyTSQueryPosition = {
	ByDepartment: (departmentId: string) =>
		`positionByDepartment:${departmentId}`
}
