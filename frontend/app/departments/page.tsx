'use client'

import { useQuery } from '@tanstack/react-query'
import { Building2, ChevronRight, GitBranch, Plus, RefreshCw } from 'lucide-react'
import Link from 'next/link'
import { useState } from 'react'

import { departmentsApi } from '@/entities/departments/api/departments.api'
import type { ParentDepartment } from '@/entities/departments/types/department.types'
import DepartmentCard from '@/entities/departments/ui/department.card'
import { directoryCatalogApi } from '@/entities/directory/api/catalog.api'
import { DepartmentForm } from '@/features/department-create/ui/department-form'
import { CatalogueSkeleton } from '@/features/catalogue-loading/ui/catalogue-skeleton'
import { getHttpStatus } from '@/shared/api/http-error'
import { useCanEdit } from '@/shared/auth/capabilities'
import { AccessState } from '@/shared/ui/access-state'

function flatten(items: ParentDepartment[]): { id: string; name: string; path: string }[] {
	return items.flatMap(department => [{ id: department.id, name: department.name, path: department.path }, ...flatten(department.children ?? [])])
}

function HierarchyBranch({ department }: { department: ParentDepartment }) {
	return <li>
		<Link className='hierarchy-node' href={`/departments/${department.id}`}><span>{department.name}</span><ChevronRight aria-hidden='true' size={15} /></Link>
		{department.children.length || department.hasMoreChildren ? <ul>{department.children.map(child => <HierarchyBranch department={child} key={child.id} />)}{department.hasMoreChildren ? <li><Link className='hierarchy-more' href={`/departments/${department.id}`}>View more teams <ChevronRight aria-hidden='true' size={14} /></Link></li> : null}</ul> : null}
	</li>
}

function DepartmentHierarchy({ departments }: { departments: ParentDepartment[] }) {
	const total = flatten(departments).length
	return <section aria-labelledby='hierarchy-title' className='hierarchy-panel'>
		<div className='hierarchy-panel__heading'><div><p className='eyebrow'>Organization map</p><h2 id='hierarchy-title'>How teams connect</h2></div><span>{total} departments</span></div>
		<ul className='hierarchy-tree'>{departments.map(department => <HierarchyBranch department={department} key={department.id} />)}</ul>
	</section>
}

export default function DepartmentsPage() {
	const canEdit = useCanEdit()
	const [creating, setCreating] = useState(false)
	const departments = useQuery({ queryKey: ['departments', 'roots'], queryFn: () => departmentsApi.getParentDepartments() })
	const locations = useQuery({ queryKey: ['locations', 'form'], queryFn: () => directoryCatalogApi.locations({ isActive: true, size: 200 }), enabled: creating })
	const accessStatus = getHttpStatus(departments.error)

	return <div className='page'>
		<header className='page-heading'><div><p className='eyebrow'>Directory service</p><h1>Organization</h1></div><div className='page-heading__actions'><p className='page-heading__description'>The department hierarchy is read from the directory service through the authenticated application boundary.</p>{canEdit ? <button className='primary-action' onClick={() => setCreating(true)} type='button'><Plus aria-hidden='true' size={16} />New department</button> : null}</div></header>
		{creating && locations.data && departments.data ? <DepartmentForm departments={flatten(departments.data)} locations={locations.data.items} onClose={() => setCreating(false)} /> : null}
		{creating && locations.isPending ? <section className='empty-state'><h2>Loading locations</h2><p>Retrieving active locations for this department.</p></section> : null}
		{departments.isPending ? <CatalogueSkeleton kind='organization' /> : null}
		{accessStatus === 401 || accessStatus === 403 ? <AccessState resource='the organization' status={accessStatus} /> : null}
		{departments.error && !accessStatus ? <section className='empty-state' role='alert'><h2>Organization data is unavailable</h2><p>Check that the local platform is running, then try again.</p><button className='retry-button' onClick={() => departments.refetch()} type='button'><RefreshCw aria-hidden='true' size={16} />Retry request</button></section> : null}
		{departments.data?.length === 0 ? <section className='empty-state'><div className='empty-state__icon'><Building2 aria-hidden='true' size={21} /></div><h2>Start the organization</h2><p>Create the first department and connect it to an operating location.</p>{canEdit ? <button className='primary-action empty-state__action' onClick={() => setCreating(true)} type='button'><Plus aria-hidden='true' size={16} />Create first department</button> : null}</section> : null}{departments.data?.length ? <><DepartmentHierarchy departments={departments.data} /><div className='directory-section-heading'><GitBranch aria-hidden='true' size={17} /><span>Department records</span></div><div className='department-grid'>{departments.data.map(department => <DepartmentCard department={department} key={department.id} />)}</div></> : null}
	</div>
}
