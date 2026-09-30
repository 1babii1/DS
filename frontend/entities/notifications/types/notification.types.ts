export type Notification = {
	body: string
	createdAt: string
	deepLink: string | null
	id: string
	isRead: boolean
	title: string
	type: string
}

export type NotificationPage = {
	hasNext: boolean
	items: Notification[]
	page: number
	size: number
	total: number
}

export type UnreadCount = { count: number }
