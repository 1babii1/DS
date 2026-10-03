import {
	canRetryAfterOverload,
	clientRetryDelayMs,
	installBackpressureRetry,
	maxBackpressureAttempts,
	rateLimitMessage,
	retryAfterMs
} from './backpressure.ts'
import axios, {
	AxiosError,
	AxiosHeaders,
	type InternalAxiosRequestConfig
} from 'axios'
import assert from 'node:assert/strict'
import { test } from 'node:test'

function overloaded(config: InternalAxiosRequestConfig) {
	return new AxiosError('overloaded', 'ERR_BAD_RESPONSE', config, undefined, {
		config,
		data: { error: { code: 'service.overloaded' } },
		headers: new AxiosHeaders({ 'retry-after': '2' }),
		status: 503,
		statusText: 'Service Unavailable'
	})
}

test('only safe reads and idempotent writes retry an overloaded service', () => {
	assert.equal(canRetryAfterOverload('get', undefined), true)
	assert.equal(canRetryAfterOverload('head', undefined), false)
	assert.equal(canRetryAfterOverload('POST', undefined), false)
	assert.equal(canRetryAfterOverload('post', 'request-id'), true)
})

test('Retry-After seconds and dates become a minimum delay with jitter', () => {
	const now = new Date('2026-10-03T06:00:00.000Z')
	assert.equal(retryAfterMs('2', now), 2000)
	assert.equal(retryAfterMs('Sat, 03 Oct 2026 06:00:03 GMT', now), 3000)
	assert.equal(retryAfterMs('invalid', now), null)
	assert.equal(
		clientRetryDelayMs(2000, () => 0.5),
		2125
	)
})

test('the overload retry budget is capped at three total attempts', () => {
	assert.equal(maxBackpressureAttempts, 3)
})

test('a rate limit tells the person when Retry-After permits another attempt', () => {
	assert.equal(
		rateLimitMessage(2_000),
		'Too many attempts. Try again in 2 seconds.'
	)
	assert.equal(
		rateLimitMessage(null),
		'Too many attempts. Try again in a minute.'
	)
})

test('an overloaded safe request respects Retry-After and succeeds on its third attempt', async () => {
	const client = axios.create()
	const delays: number[] = []
	const notices: string[] = []
	let attempts = 0
	client.defaults.adapter = async config => {
		attempts += 1
		if (attempts < 3) throw overloaded(config)
		return {
			config,
			data: { ok: true },
			headers: new AxiosHeaders(),
			status: 200,
			statusText: 'OK'
		}
	}
	installBackpressureRetry(client, {
		notify: message => notices.push(message),
		random: () => 0,
		sleep: async milliseconds => {
			delays.push(milliseconds)
		}
	})

	assert.deepEqual((await client.get('/catalog')).data, { ok: true })
	assert.equal(attempts, 3)
	assert.deepEqual(delays, [2000, 2000])
	assert.deepEqual(notices, [
		'The service is busy, retrying…',
		'The service is busy, retrying…'
	])
})

test('a non-idempotent write and a rate limit are never retried', async () => {
	for (const status of [429, 503]) {
		const client = axios.create()
		let attempts = 0
		client.defaults.adapter = async config => {
			attempts += 1
			const error = overloaded(config)
			error.response!.status = status
			throw error
		}
		installBackpressureRetry(client, {
			random: () => 0,
			sleep: async () =>
				assert.fail('unsafe requests must not sleep before retrying')
		})
		await assert.rejects(client.post('/catalog', { name: 'new' }))
		assert.equal(attempts, 1, `status ${status}`)
	}
})
