import { axiosInstance } from '@/shared/api/axiosInstance'

import type { NotificationPage, UnreadCount } from '../types/notification.types'

export const notificationsApi = {
	list: async (page = 1, unreadOnly = false) => (await axiosInstance.get<NotificationPage>('/api/notifications', { params: { page, pageSize: 20, unreadOnly } })).data,
	markAllRead: async () => axiosInstance.post('/api/notifications/read-all'),
	markRead: async (notificationId: string) => axiosInstance.post(`/api/notifications/${notificationId}/read`),
	// A ticket that opens the notification hub for about a minute and nothing else; the OAuth token stays on the server.
	hubTicket: async () => (await axiosInstance.post<{ expiresAt: string; ticket: string }>('/api/notifications/hub-ticket')).data.ticket,
	unreadCount: async () => (await axiosInstance.get<UnreadCount>('/api/notifications/unread-count')).data
}
