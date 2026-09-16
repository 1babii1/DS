'use client'

import { useQuery } from '@tanstack/react-query'
import { Activity as ActivityIcon, RefreshCw } from 'lucide-react'
import { useSearchParams } from 'next/navigation'

import { auditApi } from '@/entities/audit/api/audit.api'
import type { AuditEntry } from '@/entities/audit/types/audit-entry.types'
import { AggregateFilter } from '@/features/catalog-filter/ui/aggregate-filter'
import { CatalogPagination } from '@/features/catalog-pagination/ui/catalog-pagination'
import { getHttpStatus } from '@/shared/api/http-error'
import { AccessState } from '@/shared/ui/access-state'

function dayKey(value: string) {
	const date = new Date(value)
	return [date.getFullYear(), String(date.getMonth() + 1).padStart(2, '0'), String(date.getDate()).padStart(2, '0')].join('-')
}

function eventLabel(eventType: string) {
	return eventType.replace(/([a-z])([A-Z])/g, '$1 $2').replace(/[._-]/g, ' ')
}

function TimelineItem({ entry }: { entry: AuditEntry }) {
	const time = new Intl.DateTimeFormat('en', { timeStyle: 'short' }).format(new Date(entry.occurredAt))

	return <article className='audit-item'>
		<div className='audit-item__rail'><span /><i /></div>
		<div className='audit-item__body'>
			<div className='audit-item__meta'><span>{entry.sourceService}</span><time dateTime={entry.occurredAt}>{time}</time></div>
			<h2>{eventLabel(entry.eventType)}</h2>
			<p><span className='audit-event-tag'>{entry.eventType}</span> Aggregate <code>{entry.aggregateId}</code></p>
		</div>
	</article>
}

function Timeline({ entries }: { entries: AuditEntry[] }) {
	const groups = entries.reduce<Record<string, AuditEntry[]>>((result, entry) => {
		const key = dayKey(entry.occurredAt)
		;(result[key] ??= []).push(entry)
		return result
	}, {})

	return <section aria-label='Audit timeline' className='audit-timeline'>
		{Object.entries(groups).map(([day, events]) => <section className='audit-day' key={day}>
			<h2><time dateTime={day}>{new Intl.DateTimeFormat('en', { dateStyle: 'full' }).format(new Date(`${day}T12:00:00`))}</time><span>{events.length} event{events.length === 1 ? '' : 's'}</span></h2>
			{events.map(entry => <TimelineItem entry={entry} key={entry.id} />)}
		</section>)}
	</section>
}

export default function ActivityPage() {
	const params = useSearchParams()
	const aggregateId = params.get('aggregateId') ?? undefined
	const page = Number(params.get('page') ?? '1')
	const activity = useQuery({ queryKey: ['audit', aggregateId, page], queryFn: () => auditApi.list({ aggregateId, page }) })
	const accessStatus = getHttpStatus(activity.error)

	return <div className='page'>
		<header className='page-heading'>
			<div><p className='eyebrow'>Audit service</p><h1>Activity</h1></div>
			<p className='page-heading__description'>A chronological record of change events emitted by the platform.</p>
		</header>
		<AggregateFilter />
		{activity.isPending ? <section aria-busy='true' className='empty-state'><div className='empty-state__icon'><ActivityIcon aria-hidden='true' size={21} /></div><h2>Loading activity</h2><p>Retrieving the latest audit events.</p></section> : null}
		{accessStatus === 401 || accessStatus === 403 ? <AccessState resource='activity' status={accessStatus} /> : null}
		{activity.error && !accessStatus ? <section className='empty-state' role='alert'><div className='empty-state__icon'><ActivityIcon aria-hidden='true' size={21} /></div><h2>Activity is unavailable</h2><p>Check that the audit service is running, then try again.</p><button className='retry-button' onClick={() => activity.refetch()} type='button'><RefreshCw aria-hidden='true' size={16} />Retry request</button></section> : null}
		{activity.data?.items.length === 0 ? <section className='empty-state'><div className='empty-state__icon'><ActivityIcon aria-hidden='true' size={21} /></div><h2>No activity yet</h2><p>Events will appear here when platform services record organizational changes.</p></section> : null}
		{activity.data?.items.length ? <Timeline entries={activity.data.items} /> : null}
		{activity.data ? <CatalogPagination hasNext={activity.data.hasNext} page={activity.data.page} total={activity.data.total} /> : null}
	</div>
}
