import assert from 'node:assert/strict'
import { test } from 'node:test'

import type { Notification } from '@/entities/notifications/types/notification.types'

import { startNotificationPush, type PushConnection } from './live-push.ts'

const sample: Notification = { body: 'b', createdAt: '2026-10-01T10:00:00Z', deepLink: null, id: 'n1', isRead: false, title: 't', type: 'x' }

function fakeConnection(failStarts = 0) {
	let handler: (notification: Notification) => void = () => undefined
	let closed: () => void = () => undefined
	const calls = { starts: 0, stops: 0 }
	const connection: PushConnection = {
		on: (_event, h) => { handler = h },
		onclose: h => { closed = h },
		start: async () => {
			calls.starts++
			if (calls.starts <= failStarts) throw new Error('down')
		},
		stop: async () => { calls.stops++ }
	}
	return { calls, connection, push: (n: Notification) => handler(n), close: () => closed() }
}

const flush = () => new Promise(resolve => setImmediate(resolve))

test('a pushed notification reaches the page', async () => {
	const { connection, push } = fakeConnection()
	const received: string[] = []

	startNotificationPush({ connection, onNotification: n => received.push(n.id), wait: async () => undefined })
	await flush()
	push(sample)

	assert.deepEqual(received, ['n1'])
})

test('a failed connect is retried with growing delays and then succeeds', async () => {
	const { calls, connection } = fakeConnection(3)
	const waits: number[] = []

	startNotificationPush({ connection, onNotification: () => undefined, wait: async ms => { waits.push(ms) } })
	await flush()

	assert.equal(calls.starts, 4)
	assert.deepEqual(waits, [2_000, 5_000, 15_000])
})

test('after stop nothing is delivered and nothing reconnects', async () => {
	const { calls, close, connection, push } = fakeConnection()
	const received: string[] = []

	const stop = startNotificationPush({ connection, onNotification: n => received.push(n.id), wait: async () => undefined })
	await flush()
	stop()
	push(sample)
	close()
	await flush()

	assert.deepEqual(received, [])
	assert.equal(calls.starts, 1)
	assert.equal(calls.stops, 1)
})

test('when the connection is lost for good it starts over', async () => {
	const { calls, close, connection } = fakeConnection()

	startNotificationPush({ connection, onNotification: () => undefined, wait: async () => undefined })
	await flush()
	close()
	await flush()

	assert.equal(calls.starts, 2)
})

test('a stop while retrying ends the retries', async () => {
	const { calls, connection } = fakeConnection(100)
	let stop = () => undefined as void
	stop = startNotificationPush({
		connection,
		onNotification: () => undefined,
		wait: async () => { stop() }
	})
	await flush()

	assert.equal(calls.starts, 1)
})
