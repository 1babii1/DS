'use client'

import { useLocationsFilters } from '@/entities/locations/hooks/use-location-filters'
import { useLocationFilters } from '@/entities/locations/stores/location-filters'
import LocationFilters from '@/entities/locations/ui/location-filters'
import CardLocations from '@/entities/locations/ui/locations-card'
import { Loader2 } from 'lucide-react'
import React from 'react'

export default function PageLocations() {
	const { data, isLoading, error } = useLocationsFilters()

	const { _hasHydrated } = useLocationFilters()

	if (isLoading && !_hasHydrated)
		return (
			<div>
				<Loader2 className='h-4 w-4 animate-spin' />
				Loading...
			</div>
		)

	return (
		<div className='w-full h-full flex flex-col gap-5 px-6 py-4'>
			<h1 className='text-2xl font-bold'>Locations</h1>
			{_hasHydrated ? <LocationFilters /> : <div>Loading...</div>}
			{data?.map(location => (
				<CardLocations key={location.id} location={location} />
			))}
		</div>
	)
}
