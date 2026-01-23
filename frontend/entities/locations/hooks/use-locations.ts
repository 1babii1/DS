import { generateKeyTSQueryLocation } from '@/shared/cache/generate-key'
import { useQuery } from '@tanstack/react-query'

export function useLocations() {
	const { data, isLoading, isPending, refetch, error } = useQuery<Location[]>(
		{
			queryKey: [generateKeyTSQueryLocation.All()],
			queryFn: () => fetch('/api/locations').then(res => res.json()),
			staleTime: 1000 * 60 * 5
		}
	)
	return { data, isLoading, isPending, refetch, error }
}
