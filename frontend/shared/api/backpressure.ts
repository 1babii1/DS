import type { AxiosInstance, InternalAxiosRequestConfig } from 'axios'
import { isAxiosError } from 'axios'

declare module 'axios' {
	interface AxiosRequestConfig {
		backpressureAttempts?: number
	}
}

export const maxBackpressureAttempts = 3
export const backpressureEvent = 'ds:backpressure'

type RetryDependencies = {
	now?: () => Date
	random?: () => number
	sleep?: (milliseconds: number) => Promise<void>
	notify?: (message: string) => void
}

const retryJitterMaximumMs = 250

function hasIdempotencyKey(value: string | undefined): boolean {
	return Boolean(value?.trim())
}

export function canRetryAfterOverload(
	method: string | undefined,
	idempotencyKey: string | undefined
): boolean {
	return method?.toLowerCase() === 'get' || hasIdempotencyKey(idempotencyKey)
}

export function retryAfterMs(
	value: string | null | undefined,
	now: Date
): number | null {
	if (!value) return null
	const seconds = Number(value)
	if (Number.isFinite(seconds) && seconds >= 0)
		return Math.ceil(seconds * 1000)
	const date = Date.parse(value)
	return Number.isNaN(date) ? null : Math.max(0, date - now.getTime())
}

export function rateLimitMessage(retryAfter: number | null): string {
	if (retryAfter === null) return 'Too many attempts. Try again in a minute.'
	const seconds = Math.max(1, Math.ceil(retryAfter / 1000))
	return `Too many attempts. Try again in ${seconds} ${seconds === 1 ? 'second' : 'seconds'}.`
}

export function clientRetryDelayMs(
	retryAfter: number,
	random: () => number
): number {
	return (
		retryAfter +
		Math.floor(Math.max(0, Math.min(1, random())) * retryJitterMaximumMs)
	)
}

function headerValue(
	config: InternalAxiosRequestConfig,
	name: string
): string | undefined {
	const value = config.headers?.get(name)
	return typeof value === 'string' ? value : undefined
}

function responseHeaderValue(
	headers: Record<string, unknown>,
	name: string
): string | null {
	const value = headers[name]
	return typeof value === 'string' ? value : null
}

function defaultSleep(milliseconds: number): Promise<void> {
	return new Promise(resolve => setTimeout(resolve, milliseconds))
}

function browserNotify(message: string): void {
	if (typeof window !== 'undefined')
		window.dispatchEvent(
			new CustomEvent(backpressureEvent, { detail: message })
		)
}

export function installBackpressureRetry(
	client: AxiosInstance,
	dependencies: RetryDependencies = {}
): void {
	const now = dependencies.now ?? (() => new Date())
	const random = dependencies.random ?? Math.random
	const sleep = dependencies.sleep ?? defaultSleep
	const notify = dependencies.notify ?? browserNotify

	client.interceptors.response.use(undefined, async error => {
		if (!isAxiosError(error) || !error.config || !error.response)
			return Promise.reject(error)

		const config = error.config
		const status = error.response.status
		if (status === 429) {
			notify(
				rateLimitMessage(
					retryAfterMs(
						responseHeaderValue(error.response.headers, 'retry-after'),
						now()
					)
				)
			)
			return Promise.reject(error)
		}
		if (status !== 503) return Promise.reject(error)

		const retryAfter = retryAfterMs(
			responseHeaderValue(error.response.headers, 'retry-after'),
			now()
		)
		const attempts = config.backpressureAttempts ?? 1
		if (
			retryAfter === null ||
			!canRetryAfterOverload(
				config.method,
				headerValue(config, 'Idempotency-Key')
			) ||
			attempts >= maxBackpressureAttempts
		) {
			notify('The service is busy. Try again shortly.')
			return Promise.reject(error)
		}

		notify('The service is busy, retrying…')
		await sleep(clientRetryDelayMs(retryAfter, random))
		config.backpressureAttempts = attempts + 1
		return client.request(config)
	})
}
