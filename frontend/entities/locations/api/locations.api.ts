import { axiosInstance } from '@/shared/api/axiosInstance'
import { GetLocationsParams, Location } from '../types/location-types'

export const locationsApi = {
	GetLocations: async (params: GetLocationsParams) => {
		const response = await axiosInstance
			.get<Location[]>('lication/search', {
				params: {
					search: params.search,
					page: params.page ?? 1,
					size: params.size ?? 10
				}
			})
			.then(res => res.data)
			.catch(() => [])
		return response
	}
}
