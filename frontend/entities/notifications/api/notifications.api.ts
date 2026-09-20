import { axiosInstance } from '@/shared/api/axiosInstance'

import type { NotificationPage, UnreadCount } from '../types/notification.types'

export const notificationsApi = {
	list: async () => (await axiosInstance.get<NotificationPage>('/api/notifications', { params: { page: 1, pageSize: 20 } })).data,
	markAllRead: async () => axiosInstance.post('/api/notifications/read-all'),
	markRead: async (notificationId: string) => axiosInstance.post(`/api/notifications/${notificationId}/read`),
	unreadCount: async () => (await axiosInstance.get<UnreadCount>('/api/notifications/unread-count')).data
}
