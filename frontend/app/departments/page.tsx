'use client'

import { isAxiosError } from 'axios'
import { Building2, LogIn, RefreshCw } from 'lucide-react'
import Link from 'next/link'
import { useQuery } from '@tanstack/react-query'

import { departmentsApi } from '@/entities/departments/api/departments.api'
import type { ParentDepartment } from '@/entities/departments/types/department.types'
import DepartmentCard from '@/entities/departments/ui/department.card'

export default function DepartmentsPage() {
	const { data, error, isPending, refetch } = useQuery<ParentDepartment[]>({
		queryFn: () => departmentsApi.getParentDepartments(),
		queryKey: ['departments', 'roots']
	})
	const requiresSignIn = isAxiosError(error) && error.response?.status === 401

	return <div className='page'>
		<header className='page-heading'><div><p className='eyebrow'>Directory service</p><h1>Organization</h1></div><p className='page-heading__description'>The department hierarchy is read from the directory service through the authenticated application boundary.</p></header>
		{isPending ? <section aria-busy='true' className='empty-state'><div className='empty-state__icon'><Building2 aria-hidden='true' size={21} /></div><h2>Loading organization structure</h2><p>The directory service is responding with the current department hierarchy.</p></section> : null}
		{error && requiresSignIn ? <section className='empty-state'><div className='empty-state__icon'><LogIn aria-hidden='true' size={21} /></div><h2>Sign in to view the organization</h2><p>Your workspace session gives the server permission to retrieve protected directory data without exposing credentials in the browser.</p><Link className='retry-button' href='/login'><LogIn aria-hidden='true' size={16} />Continue to sign in</Link></section> : null}
		{error && !requiresSignIn ? <section className='empty-state' role='alert'><div className='empty-state__icon'><Building2 aria-hidden='true' size={21} /></div><h2>Organization data is unavailable</h2><p>Check that the local platform is running, then try again.</p><button className='retry-button' onClick={() => refetch()} type='button'><RefreshCw aria-hidden='true' size={16} />Retry request</button></section> : null}
		{data ? <div className='department-grid'>{data.map(department => <DepartmentCard department={department} key={department.id} />)}</div> : null}
	</div>
}
