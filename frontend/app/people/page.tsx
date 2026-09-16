'use client'

import { useQuery } from '@tanstack/react-query'
import { Building2, Plus, RefreshCw, Repeat2, SlidersHorizontal, UsersRound, X } from 'lucide-react'
import { usePathname, useRouter, useSearchParams } from 'next/navigation'
import { useMemo, useState } from 'react'

import { directoryCatalogApi } from '@/entities/directory/api/catalog.api'
import { employeesApi } from '@/entities/employees/api/employees.api'
import type { Employee } from '@/entities/employees/types/employee.types'
import { CatalogPagination } from '@/features/catalog-pagination/ui/catalog-pagination'
import { EmployeeForm } from '@/features/employee-lifecycle/ui/employee-form'
import { CatalogueSkeleton } from '@/features/catalogue-loading/ui/catalogue-skeleton'
import { getHttpStatus } from '@/shared/api/http-error'
import { AccessState } from '@/shared/ui/access-state'

function initials(name: string) {
	return name.split(' ').map(part => part[0]).slice(0, 2).join('').toUpperCase()
}

function PersonCard({ employee, onTransfer }: { employee: Employee; onTransfer: (employee: Employee) => void }) {
	return <article className='person-card'>
		<div className='person-card__avatar'>{initials(employee.fullName)}</div>
		<div className='person-card__top'><div><h2>{employee.fullName}</h2><a href={`mailto:${employee.email}`}>{employee.email}</a></div><span className='status-badge'>{employee.status}</span></div>
		<dl><div><dt>Position</dt><dd>{employee.positionName}</dd></div><div><dt>Department</dt><dd>{employee.departmentName}</dd></div><div><dt>Joined</dt><dd>{new Intl.DateTimeFormat('en', { dateStyle: 'medium' }).format(new Date(employee.hiredAt))}</dd></div></dl>
		<button className='person-card__action' onClick={() => onTransfer(employee)} type='button'><Repeat2 aria-hidden='true' size={15} />Transfer</button>
	</article>
}

export default function PeoplePage() {
	const params = useSearchParams()
	const pathname = usePathname()
	const router = useRouter()
	const page = Number(params.get('page') ?? '1')
	const departmentId = params.get('department') ?? ''
	const positionId = params.get('position') ?? ''
	const [form, setForm] = useState<{ employee?: Employee } | null>(null)
	const people = useQuery({ queryKey: ['employees', departmentId, page], queryFn: () => employeesApi.list(departmentId || undefined, page) })
	const positions = useQuery({ queryKey: ['positions', 'active'], queryFn: () => directoryCatalogApi.positions({ isActive: true, size: 200 }) })
	const departments = useMemo(() => Array.from(new Map((positions.data?.items ?? []).flatMap(position => position.departments).map(department => [department.id, department])).values()).sort((a, b) => a.name.localeCompare(b.name)), [positions.data])
	const visiblePeople = useMemo(() => people.data?.items.filter(person => !positionId || person.positionId === positionId) ?? [], [people.data, positionId])
	const accessStatus = getHttpStatus(people.error)
	const updateFilter = (name: 'department' | 'position', value: string) => {
		const next = new URLSearchParams(params.toString())
		if (value) next.set(name, value)
		else next.delete(name)
		next.delete('page')
		router.replace(`${pathname}${next.size ? `?${next}` : ''}`)
	}
	const clearFilters = () => router.replace(pathname)
	const hasFilters = Boolean(departmentId || positionId)

	return <div className='page'>
		<header className='page-heading'><div><p className='eyebrow'>Employee service</p><h1>People</h1></div><div className='page-heading__actions'><p className='page-heading__description'>People records are retrieved through the authenticated service boundary.</p><button className='primary-action' onClick={() => setForm({})} type='button'><Plus aria-hidden='true' size={16} />Hire employee</button></div></header>
		{form && positions.data ? <EmployeeForm employee={form.employee ? { id: form.employee.id, name: form.employee.fullName } : undefined} onClose={() => setForm(null)} positions={positions.data.items} /> : null}
		{form && positions.isPending ? <section className='empty-state'><h2>Loading organization options</h2><p>Retrieving active positions and their department links.</p></section> : null}
		{people.data ? <section aria-label='People directory filters' className='directory-filters'><div className='directory-filters__heading'><SlidersHorizontal aria-hidden='true' size={16} /><span>Directory filters</span></div><label><span>Department</span><select onChange={event => updateFilter('department', event.target.value)} value={departmentId}><option value=''>All departments</option>{departments.map(department => <option key={department.id} value={department.id}>{department.name}</option>)}</select></label><label><span>Position</span><select onChange={event => updateFilter('position', event.target.value)} value={positionId}><option value=''>All positions</option>{(positions.data?.items ?? []).map(position => <option key={position.id} value={position.id}>{position.name}</option>)}</select></label>{hasFilters ? <button className='filter-reset' onClick={clearFilters} type='button'><X aria-hidden='true' size={14} />Clear filters</button> : null}</section> : null}
		{people.isPending ? <CatalogueSkeleton kind='people' /> : null}
		{accessStatus === 401 || accessStatus === 403 ? <AccessState resource='people' status={accessStatus} /> : null}
		{people.error && !accessStatus ? <section className='empty-state' role='alert'><div className='empty-state__icon'><Building2 aria-hidden='true' size={21} /></div><h2>People data is unavailable</h2><p>Check that the employee service is running, then try again.</p><button className='retry-button' onClick={() => people.refetch()} type='button'><RefreshCw aria-hidden='true' size={16} />Retry request</button></section> : null}
		{people.data?.items.length === 0 ? <section className='empty-state'><div className='empty-state__icon'><UsersRound aria-hidden='true' size={21} /></div><h2>No people yet</h2><p>Start the directory by hiring the first employee into an active department and position.</p><button className='primary-action empty-state__action' onClick={() => setForm({})} type='button'><Plus aria-hidden='true' size={16} />Hire first employee</button></section> : null}
		{Boolean(people.data?.items.length) && visiblePeople.length === 0 ? <section className='empty-state'><div className='empty-state__icon'><SlidersHorizontal aria-hidden='true' size={21} /></div><h2>No matching people</h2><p>Try another position or clear the active filters to view the full directory.</p><button className='retry-button' onClick={clearFilters} type='button'>Clear filters</button></section> : null}
		{visiblePeople.length ? <section aria-label='People directory' className='people-grid'>{visiblePeople.map(employee => <PersonCard employee={employee} key={employee.id} onTransfer={employee => setForm({ employee })} />)}</section> : null}
		{people.data ? <CatalogPagination hasNext={people.data.hasNext} page={people.data.page} total={people.data.total} /> : null}
	</div>
}
