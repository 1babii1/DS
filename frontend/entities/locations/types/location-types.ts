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
	search: string
	page?: number
	size?: number
}
