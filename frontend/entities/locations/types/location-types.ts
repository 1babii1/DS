export interface Location {
	id: string
	name: string
	timezone: string
	street: string
	city: string
	country: string
	isActive: boolean
	createdAt: Date
	updatedAt: Date
}

export type GetLocationsParams = {
	isActive?: string
	departmentId?: string[]
	search?: string
	page?: string
	size?: string
	sortBy?: string
	sortDirection?: string
}
