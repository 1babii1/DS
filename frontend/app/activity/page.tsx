'use client'

import { useQuery } from '@tanstack/react-query'
import { isAxiosError } from 'axios'
import { Activity as ActivityIcon, LogIn, RefreshCw } from 'lucide-react'
import Link from 'next/link'
import { useSearchParams } from 'next/navigation'

import { auditApi } from '@/entities/audit/api/audit.api'
import type { AuditEntry } from '@/entities/audit/types/audit-entry.types'
import { AggregateFilter } from '@/features/catalog-filter/ui/aggregate-filter'
import { CatalogPagination } from '@/features/catalog-pagination/ui/catalog-pagination'

function dayKey(value: string) { return new Date(value).toISOString().slice(0, 10) }
function eventLabel(eventType: string) { return eventType.replace(/([a-z])([A-Z])/g, '$1 $2').replace(/[._-]/g, ' ') }
function TimelineItem({ entry }: { entry: AuditEntry }) {
	const time = new Intl.DateTimeFormat('en', { timeStyle: 'short' }).format(new Date(entry.occurredAt))
	return <article className='audit-item'><div className='audit-item__rail'><span /><i /></div><div className='audit-item__body'><div className='audit-item__meta'><span>{entry.sourceService}</span><time dateTime={entry.occurredAt}>{time}</time></div><h2>{eventLabel(entry.eventType)}</h2><p><span className='audit-event-tag'>{entry.eventType}</span> Aggregate <code>{entry.aggregateId}</code></p></div></article>
}
function Timeline({ entries }: { entries: AuditEntry[] }) {
	const groups = entries.reduce<Record<string, AuditEntry[]>>((result, entry) => { const key = dayKey(entry.occurredAt); (result[key] ??= []).push(entry); return result }, {})
	return <section aria-label='Audit timeline' className='audit-timeline'>{Object.entries(groups).map(([day, events]) => <section className='audit-day' key={day}><h2><time dateTime={day}>{new Intl.DateTimeFormat('en', { dateStyle: 'full' }).format(new Date(`${day}T12:00:00Z`))}</time><span>{events.length} event{events.length === 1 ? '' : 's'}</span></h2>{events.map(entry => <TimelineItem entry={entry} key={entry.id} />)}</section>)}</section>
}

export default function ActivityPage() {
	const params = useSearchParams(); const aggregateId = params.get('aggregateId') ?? undefined; const page = Number(params.get('page') ?? '1')
	const { data, error, isPending, refetch } = useQuery({ queryKey: ['audit', aggregateId, page], queryFn: () => auditApi.list({ aggregateId, page }) })
	const signIn = isAxiosError(error) && error.response?.status === 401
	return <div className='page'><header className='page-heading'><div><p className='eyebrow'>Audit service</p><h1>Activity</h1></div><p className='page-heading__description'>A chronological record of change events emitted by the platform.</p></header><AggregateFilter />{isPending ? <section aria-busy='true' className='empty-state'><div className='empty-state__icon'><ActivityIcon aria-hidden='true' size={21} /></div><h2>Loading activity</h2><p>Retrieving the latest audit events.</p></section> : null}{signIn ? <section className='empty-state'><div className='empty-state__icon'><LogIn aria-hidden='true' size={21} /></div><h2>Sign in to view activity</h2><p>Audit records are available through an authenticated workspace session.</p><Link className='retry-button' href='/login'><LogIn aria-hidden='true' size={16} />Continue to sign in</Link></section> : null}{error && !signIn ? <section className='empty-state' role='alert'><div className='empty-state__icon'><ActivityIcon aria-hidden='true' size={21} /></div><h2>Activity is unavailable</h2><p>Check that the audit service is running, then try again.</p><button className='retry-button' onClick={() => refetch()} type='button'><RefreshCw aria-hidden='true' size={16} />Retry request</button></section> : null}{data?.items.length === 0 ? <section className='empty-state'><div className='empty-state__icon'><ActivityIcon aria-hidden='true' size={21} /></div><h2>No activity yet</h2><p>Events will appear here when platform services record organizational changes.</p></section> : null}{data?.items.length ? <Timeline entries={data.items} /> : null}{data ? <CatalogPagination hasNext={data.hasNext} page={data.page} total={data.total} /> : null}</div>
}
