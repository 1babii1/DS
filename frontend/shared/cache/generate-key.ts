export const generateKeyTSQueryDepartment = {
	ById: (id: string) => `department:${id}`,
	BySearch: (search: string) => `departmentBySearch:${search}`,
	Children: (parentId: string) => `departmentChildren:${parentId}`,
	ByLocation: (locationIds?: string[]) =>
		`departmentByLocation:${locationIds?.join(',')}`,
	TopByPositions: () => 'DepartmentsTopByPositions'
}

export const generateKeyTSQueryLocation = {
	BySearch: (search: string) => `locationBySearch:${search}`
}

export const generateKeyTSQueryPosition = {
	ByDepartment: (departmentId: string) =>
		`positionByDepartment:${departmentId}`
}
