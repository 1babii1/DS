import { axiosInstance } from '@/shared/api/axiosInstance'
import { GetLocationsParams, Location } from '../types/location-types'

export const locationsApi = {
	GetLocationsWithFilters: async (params: GetLocationsParams) => {
		const response = await axiosInstance
			.get<Location[]>('api/locations', {
				params: {
					IsActive: params.isActive,
					DepartmentId: params.departmentId,
					Search: params.search,
					Page: params.page ?? 1,
					PageSize: params.size ?? 10,
					SortBy: params.sortBy ?? 'created_at',
					SortDirection: params.sortDirection ?? 'ASC'
				}
			})
			.then(res => res.data)
		return response
	}
}
