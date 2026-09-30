import { axiosInstance } from '@/shared/api/axiosInstance'

import type { NotificationPage, UnreadCount } from '../types/notification.types'

export const notificationsApi = {
	list: async (page = 1, unreadOnly = false) => (await axiosInstance.get<NotificationPage>('/api/notifications', { params: { page, pageSize: 20, unreadOnly } })).data,
	markAllRead: async () => axiosInstance.post('/api/notifications/read-all'),
	markRead: async (notificationId: string) => axiosInstance.post(`/api/notifications/${notificationId}/read`),
	unreadCount: async () => (await axiosInstance.get<UnreadCount>('/api/notifications/unread-count')).data
}
