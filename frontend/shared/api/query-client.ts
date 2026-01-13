import { QueryClient } from '@tanstack/react-query'

export const queryClient = new QueryClient({
	defaultOptions: {
		queries: {
			refetchOnWindowFocus: false,
			staleTime: 1 * 60 * 1000 // 1 minutes
		}
	}
})
