'use client'

import { useQuery } from '@tanstack/react-query'
import { isAxiosError } from 'axios'
import { Building2, ChevronRight, GitBranch, LogIn, Plus, RefreshCw } from 'lucide-react'
import Link from 'next/link'
import { useState } from 'react'

import { departmentsApi } from '@/entities/departments/api/departments.api'
import type { ParentDepartment } from '@/entities/departments/types/department.types'
import DepartmentCard from '@/entities/departments/ui/department.card'
import { directoryCatalogApi } from '@/entities/directory/api/catalog.api'
import { DepartmentForm } from '@/features/department-create/ui/department-form'
import { CatalogueSkeleton } from '@/features/catalogue-loading/ui/catalogue-skeleton'

function flatten(items: ParentDepartment[]): { id: string; name: string; path: string }[] {
	return items.flatMap(department => [{ id: department.id, name: department.name, path: department.path }, ...flatten(department.children ?? [])])
}

function HierarchyBranch({ department }: { department: ParentDepartment }) {
	return <li>
		<Link className='hierarchy-node' href={`/departments/${department.id}`}><span>{department.name}</span><ChevronRight aria-hidden='true' size={15} /></Link>
		{department.children.length ? <ul>{department.children.map(child => <HierarchyBranch department={child} key={child.id} />)}</ul> : null}
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
	const [creating, setCreating] = useState(false)
	const departments = useQuery({ queryKey: ['departments', 'roots'], queryFn: () => departmentsApi.getParentDepartments() })
	const locations = useQuery({ queryKey: ['locations', 'form'], queryFn: () => directoryCatalogApi.locations({ isActive: true, size: 200 }), enabled: creating })
	const requiresSignIn = isAxiosError(departments.error) && departments.error.response?.status === 401

	return <div className='page'>
		<header className='page-heading'><div><p className='eyebrow'>Directory service</p><h1>Organization</h1></div><div className='page-heading__actions'><p className='page-heading__description'>The department hierarchy is read from the directory service through the authenticated application boundary.</p><button className='primary-action' onClick={() => setCreating(true)} type='button'><Plus aria-hidden='true' size={16} />New department</button></div></header>
		{creating && locations.data && departments.data ? <DepartmentForm departments={flatten(departments.data)} locations={locations.data.items} onClose={() => setCreating(false)} /> : null}
		{creating && locations.isPending ? <section className='empty-state'><h2>Loading locations</h2><p>Retrieving active locations for this department.</p></section> : null}
		{departments.isPending ? <CatalogueSkeleton kind='organization' /> : null}
		{requiresSignIn ? <section className='empty-state'><div className='empty-state__icon'><LogIn aria-hidden='true' size={21} /></div><h2>Sign in to view the organization</h2><p>Your workspace session gives the server permission to retrieve protected directory data.</p><Link className='retry-button' href='/login'><LogIn aria-hidden='true' size={16} />Continue to sign in</Link></section> : null}
		{departments.error && !requiresSignIn ? <section className='empty-state' role='alert'><h2>Organization data is unavailable</h2><p>Check that the local platform is running, then try again.</p><button className='retry-button' onClick={() => departments.refetch()} type='button'><RefreshCw aria-hidden='true' size={16} />Retry request</button></section> : null}
		{departments.data?.length === 0 ? <section className='empty-state'><div className='empty-state__icon'><Building2 aria-hidden='true' size={21} /></div><h2>Start the organization</h2><p>Create the first department and connect it to an operating location.</p><button className='primary-action empty-state__action' onClick={() => setCreating(true)} type='button'><Plus aria-hidden='true' size={16} />Create first department</button></section> : null}{departments.data?.length ? <><DepartmentHierarchy departments={departments.data} /><div className='directory-section-heading'><GitBranch aria-hidden='true' size={17} /><span>Department records</span></div><div className='department-grid'>{departments.data.map(department => <DepartmentCard department={department} key={department.id} />)}</div></> : null}
	</div>
}
