import { isAxiosError } from 'axios'

import { rateLimitMessage, retryAfterMs } from './backpressure'

export function mutationErrorMessage(error: unknown, action: string) {
	if (isAxiosError(error) && error.response?.status === 429) {
		const retryAfter = error.response.headers['retry-after']
		return rateLimitMessage(
			typeof retryAfter === 'string'
				? retryAfterMs(retryAfter, new Date())
				: null
		)
	}
	if (isAxiosError(error) && error.response?.status === 503)
		return 'The service is busy. Try again shortly.'
	if (isAxiosError(error) && error.response?.status === 403)
		return 'Your role does not have permission to make this change.'
	if (isAxiosError(error) && error.response?.status === 401)
		return 'Your session has expired. Sign in again to continue.'
	return `${action} could not be completed. Check the selected data and try again.`
}
