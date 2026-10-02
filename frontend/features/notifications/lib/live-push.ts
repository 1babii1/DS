import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr'

import type { Notification } from '@/entities/notifications/types/notification.types'

// What the page needs from a hub connection; the SignalR client satisfies it, and a test can stand in for it.
export type PushConnection = {
	on: (event: 'notification', handler: (notification: Notification) => void) => void
	onclose: (handler: () => void) => void
	start: () => Promise<void>
	stop: () => Promise<void>
}

// The ticket is asked for again on every (re)connect: it lives about a minute and opens the hub only. The browser
// never holds the OAuth token. Logging stays at errors because the debug levels print the connection URL, which
// carries the ticket.
export function hubConnection(url: string, getTicket: () => Promise<string>): PushConnection {
	return new HubConnectionBuilder()
		.withUrl(url, { accessTokenFactory: getTicket, withCredentials: false })
		.withAutomaticReconnect([0, 2_000, 10_000, 30_000])
		.configureLogging(LogLevel.Error)
		.build()
}

const retryDelaysMs = [2_000, 5_000, 15_000, 30_000, 60_000]

type Options = {
	connection: PushConnection
	onNotification: (notification: Notification) => void
	// Injected so a test does not wait in real time.
	wait?: (milliseconds: number) => Promise<void>
}

// Connects, and keeps trying with a growing delay until stopped: a failure to connect (service down, ticket refused)
// must not become a tight loop, and must not break the page, which still has the polled feed. Returns the stop
// function; after it is called nothing is delivered and nothing reconnects.
export function startNotificationPush({ connection, onNotification, wait = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds)) }: Options): () => void {
	let stopped = false

	connection.on('notification', notification => {
		if (!stopped) onNotification(notification)
	})

	const connect = async () => {
		for (let attempt = 0; !stopped; attempt++) {
			try {
				await connection.start()
				return
			} catch {
				await wait(retryDelaysMs[Math.min(attempt, retryDelaysMs.length - 1)])
			}
		}
	}

	// Automatic reconnect gives up after its own schedule; start over rather than leave the page without push.
	connection.onclose(() => void connect())

	void connect()

	return () => {
		stopped = true
		void connection.stop().catch(() => undefined)
	}
}
