import { isAxiosError } from 'axios'

export function getHttpStatus(error: unknown): number | undefined {
	return isAxiosError(error) ? error.response?.status : undefined
}

export function isAuthenticationError(error: unknown): boolean {
	return getHttpStatus(error) === 401
}

export function isAuthorizationError(error: unknown): boolean {
	return getHttpStatus(error) === 403
}
