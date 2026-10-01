'use client'

import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { History as HistoryIcon, RefreshCw, UsersRound } from 'lucide-react'
import { useState } from 'react'

import { orgChartApi } from '@/entities/org-history/api/org-chart.api'
import { getHttpStatus } from '@/shared/api/http-error'
import { AccessState } from '@/shared/ui/access-state'

import { buildTree, dateAtOffset, daySpan, headcount, type TreeNode } from '../lib/org-timeline'

function Department({ node }: { node: TreeNode }) {
	return <li className='history-node'>
		<div className='history-node__head'>
			<strong>{node.name}</strong>
			<span aria-label={`${headcount(node)} people including sub-departments`}><UsersRound aria-hidden='true' size={13} />{headcount(node)}</span>
		</div>
		{node.people.length ? <ul className='history-people'>
			{node.people.map(person => <li key={person.id}>{person.name}<span>{person.position}</span></li>)}
		</ul> : null}
		{node.children.length ? <ul className='history-tree'>{node.children.map(child => <Department key={child.id} node={child} />)}</ul> : null}
	</li>
}

// The org on a chosen day. The slider runs from the day of the first recorded event to today; each position asks the
// audit service for the org as it stood when that day ended. The tree shown is always what the service answered for
// the date on the label, and keeps the previous answer on screen while the next one loads, so dragging does not flicker.
export function OrgHistoryView() {
	const [offset, setOffset] = useState<number | null>(null)
	const start = useQuery({ queryKey: ['org-chart', 'now'], queryFn: () => orgChartApi.at() })
	const first = start.data?.firstEventAt ?? null
	const span = daySpan(first, new Date())
	const position = offset === null ? span : Math.min(offset, span)
	const date = first ? dateAtOffset(first, position) : undefined

	const chart = useQuery({
		queryKey: ['org-chart', date],
		queryFn: () => orgChartApi.at(date),
		enabled: Boolean(date),
		placeholderData: keepPreviousData
	})

	const status = getHttpStatus(start.error) ?? getHttpStatus(chart.error)
	if (status === 401 || status === 403) return <AccessState resource='the org history' status={status} />

	if (start.isPending) {
		return <section aria-busy='true' className='empty-state'><div className='empty-state__icon'><HistoryIcon aria-hidden='true' size={21} /></div><h2>Loading history</h2><p>Reading the recorded organization changes.</p></section>
	}

	if (start.error) {
		return <section className='empty-state' role='alert'><div className='empty-state__icon'><HistoryIcon aria-hidden='true' size={21} /></div><h2>History is unavailable</h2><p>Check that the audit service is running, then try again.</p><button className='retry-button' onClick={() => start.refetch()} type='button'><RefreshCw aria-hidden='true' size={16} />Retry request</button></section>
	}

	if (!first) {
		return <section className='empty-state'><div className='empty-state__icon'><HistoryIcon aria-hidden='true' size={21} /></div><h2>No history recorded yet</h2><p>The organization&apos;s past appears here once the audit service has recorded changes to departments and people.</p></section>
	}

	const tree = chart.data ? buildTree(chart.data.departments) : []

	return <div className='history'>
		<div className='history-slider'>
			<label htmlFor='history-date'>Organization on</label>
			<output htmlFor='history-date'>{new Intl.DateTimeFormat('en', { dateStyle: 'long', timeZone: 'UTC' }).format(new Date(`${date}T00:00:00Z`))}</output>
			<input aria-valuetext={date} id='history-date' max={span} min={0} onChange={event => setOffset(Number(event.target.value))} step={1} type='range' value={position} />
			<div className='history-slider__ends'><span>{dateAtOffset(first, 0)}</span><span>today</span></div>
		</div>
		{chart.error ? <p role='alert'>This date could not be loaded. Move the slider to try again.</p> : null}
		{chart.data && tree.length === 0 ? <section className='empty-state'><h2>No departments yet on this date</h2><p>Nothing had been recorded by then. Move the slider forward.</p></section> : null}
		{tree.length ? <ul aria-busy={chart.isPlaceholderData} className='history-tree history-tree--root'>{tree.map(node => <Department key={node.id} node={node} />)}</ul> : null}
		{chart.data?.unplaced.length ? <section className='history-unplaced'>
			<h2>Not placed in a department</h2>
			<p>Their last recorded department did not exist on this date.</p>
			<ul className='history-people'>{chart.data.unplaced.map(person => <li key={person.id}>{person.name}<span>{person.position}</span></li>)}</ul>
		</section> : null}
		{chart.data?.skippedEvents ? <p className='history-note'>{chart.data.skippedEvents} recorded event(s) could not be read, so this view may be incomplete.</p> : null}
	</div>
}
