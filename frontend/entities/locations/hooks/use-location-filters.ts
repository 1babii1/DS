import { useQuery } from '@tanstack/react-query'
import { useLocationFilters } from '../stores/location-filters'
import { generateKeyTSQueryLocation } from '@/shared/cache/generate-key'
import { locationsApi } from '../api/locations.api'
import { Location } from '../types/location-types'

export function useLocationsFilters() {
	const filters = useLocationFilters()

	const { data, isLoading, isPending, refetch, error } = useQuery<Location[]>(
		{
			queryKey: [
				generateKeyTSQueryLocation.ByFilters(
					filters.search,
					filters.sortBy,
					filters.pageSize,
					filters.isActive,
					filters.departmentId,
					filters.sortDirection
				)
			],
			queryFn: () => locationsApi.GetLocations(filters),
			staleTime: 5 * 60 * 1000
		}
	)

	return { data, isLoading, isPending, refetch, error }
}
