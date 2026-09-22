'use client'

import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Bell, CheckCheck, LoaderCircle } from 'lucide-react'
import { useRouter } from 'next/navigation'
import { useState } from 'react'

import { notificationsApi } from '@/entities/notifications/api/notifications.api'
import type { Notification } from '@/entities/notifications/types/notification.types'
import { getHttpStatus } from '@/shared/api/http-error'
import { Sheet, SheetContent, SheetTitle, SheetTrigger } from '@/shared/ui/sheet'

function relativeTime(value: string) {
	const difference = Date.now() - new Date(value).getTime()
	const minutes = Math.max(0, Math.round(difference / 60_000))
	if (minutes < 1) return 'Just now'
	if (minutes < 60) return `${minutes}m ago`
	const hours = Math.round(minutes / 60)
	if (hours < 24) return `${hours}h ago`
	return new Intl.DateTimeFormat('en', { dateStyle: 'medium' }).format(new Date(value))
}

function isInternalDeepLink(value: string | null) {
	return Boolean(value?.startsWith('/') && !value.startsWith('//'))
}

export function NotificationBell({ enabled }: { enabled: boolean }) {
	const [open, setOpen] = useState(false)
	const router = useRouter()
	const queryClient = useQueryClient()
	const notifications = useInfiniteQuery({ queryKey: ['notifications'], queryFn: ({ pageParam }) => notificationsApi.list(pageParam), initialPageParam: 1, getNextPageParam: page => page.hasNext ? page.page + 1 : undefined, enabled: enabled && open })
	const unreadCount = useQuery({ queryKey: ['notifications', 'unread-count'], queryFn: notificationsApi.unreadCount, enabled, refetchInterval: 30_000 })
	const refresh = () => Promise.all([
		queryClient.invalidateQueries({ queryKey: ['notifications'] }),
		queryClient.invalidateQueries({ queryKey: ['notifications', 'unread-count'] })
	])
	const markRead = useMutation({ mutationFn: notificationsApi.markRead, onSuccess: refresh })
	const markAllRead = useMutation({ mutationFn: notificationsApi.markAllRead, onSuccess: refresh })
	const count = unreadCount.data?.count ?? 0
	const status = getHttpStatus(notifications.error ?? unreadCount.error)
	const notificationItems = notifications.data?.pages.flatMap(page => page.items) ?? []

	if (!enabled || status === 401 || status === 403) return null

	const openNotification = (notification: Notification) => {
		if (!notification.isRead) markRead.mutate(notification.id)
		if (notification.deepLink && isInternalDeepLink(notification.deepLink)) router.push(notification.deepLink)
	}

	return <Sheet onOpenChange={setOpen} open={open}>
		<SheetTrigger asChild>
			<button aria-label={count ? `Open notifications, ${count} unread` : 'Open notifications'} className='notification-trigger icon-button' type='button'>
				<Bell aria-hidden='true' size={17} />
				{count ? <span aria-hidden='true' className='notification-trigger__count'>{count > 99 ? '99+' : count}</span> : null}
			</button>
		</SheetTrigger>
		<SheetContent aria-describedby='notification-description' className='notification-sheet'>
			<div className='notification-sheet__heading'><div><p className='eyebrow'>Notification center</p><SheetTitle>Updates for you</SheetTitle><p id='notification-description'>Changes from your workspace and rewards activity.</p></div>{count ? <button className='filter-reset' disabled={markAllRead.isPending} onClick={() => markAllRead.mutate()} type='button'><CheckCheck aria-hidden='true' size={15} />Mark all read</button> : null}</div>
			{notifications.isPending ? <div className='notification-sheet__state'><LoaderCircle aria-hidden='true' className='animate-spin' size={18} />Loading notifications…</div> : null}
			{notifications.error && status !== 401 && status !== 403 ? <div className='notification-sheet__state' role='alert'>Notifications are temporarily unavailable. <button onClick={() => notifications.refetch()} type='button'>Try again</button></div> : null}
			{markRead.error || markAllRead.error ? <p className='notification-sheet__error' role='alert'>The notification state could not be updated. Try again.</p> : null}
			{notifications.data && notificationItems.length === 0 ? <div className='notification-sheet__state'>You&apos;re all caught up.</div> : null}
			{notificationItems.length ? <><ol className='notification-list'>{notificationItems.map(notification => <li className={notification.isRead ? '' : 'notification-list__item--unread'} key={notification.id}><button onClick={() => openNotification(notification)} type='button'><span><strong>{notification.title}</strong><small>{notification.body}</small></span><time dateTime={notification.createdAt}>{relativeTime(notification.createdAt)}</time></button></li>)}</ol>{notifications.hasNextPage ? <button className='notification-sheet__more' disabled={notifications.isFetchingNextPage} onClick={() => notifications.fetchNextPage()} type='button'>{notifications.isFetchingNextPage ? 'Loading more…' : 'Load more'}</button> : null}</> : null}
		</SheetContent>
	</Sheet>
}
