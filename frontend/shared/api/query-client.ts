import { QueryClient } from '@tanstack/react-query'

import { getHttpStatus } from './http-error'

export const queryClient = new QueryClient({
	defaultOptions: {
		queries: {
			refetchOnWindowFocus: false,
			staleTime: 60 * 1000,
			retry: (failureCount, error) => {
				const status = getHttpStatus(error)

				if (status && status < 500) return false

				return failureCount < 1
			},
			retryDelay: 500
		}
	}
})
