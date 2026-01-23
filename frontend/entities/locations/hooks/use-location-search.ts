import { useQuery } from '@tanstack/react-query'
import { useEffect, useState } from 'react'
import { LocationForm } from '@/entities/departments/types/department.types'
import { locationsApi } from '../api/locations.api'

interface Props {
	locationSearch: string
	page?: string
	size?: string
	debounceMs?: number
}

export function useLocationSearch({
	locationSearch,
	page,
	size,
	debounceMs = 300
}: Props) {
	const [debounceSearch, setDebounceSearch] = useState(locationSearch)

	useEffect(() => {
		const timer = setTimeout(() => {
			setDebounceSearch(locationSearch)
		}, debounceMs)
		return () => clearTimeout(timer)
	}, [locationSearch, debounceMs])

	const {
		data: locations = [] as LocationForm[],
		isFetching: isLocationsFetching
	} = useQuery({
		queryKey: ['locations', debounceSearch],
		queryFn: () =>
			locationsApi.GetLocationsWithFilters({
				search: debounceSearch,
				page: page ?? '1',
				size: size ?? '20'
			}),
		enabled: !!debounceSearch,
		staleTime: 0
	})
	return {
		locations,
		isLocationsFetching
	}
}
