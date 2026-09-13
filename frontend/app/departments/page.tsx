'use client'

import { Building2, RefreshCw } from 'lucide-react'
import { useQuery } from '@tanstack/react-query'
import { departmentsApi } from '@/entities/departments/api/departments.api'
import type { ParentDepartment } from '@/entities/departments/types/department.types'
import DepartmentCard from '@/entities/departments/ui/department.card'

export default function DepartmentsPage() {
	const { data, error, isPending, refetch } = useQuery<ParentDepartment[]>({ queryFn: () => departmentsApi.getParentDepartments(), queryKey: ['departments', 'roots'] })
	return <div className='page'>
		<header className='page-heading'><div><p className='eyebrow'>Directory service</p><h1>Organization</h1></div><p className='page-heading__description'>The department hierarchy is read directly from the directory service.</p></header>
		{isPending ? <section aria-busy='true' className='empty-state'><div className='empty-state__icon'><Building2 aria-hidden='true' size={21} /></div><h2>Loading organization structure</h2><p>The directory service is responding with the current department hierarchy.</p></section> : null}
		{error ? <section className='empty-state' role='alert'><div className='empty-state__icon'><Building2 aria-hidden='true' size={21} /></div><h2>Organization data is unavailable</h2><p>Check that the local platform is running, then try again.</p><button className='retry-button' onClick={() => refetch()} type='button'><RefreshCw aria-hidden='true' size={16} />Retry request</button></section> : null}
		{data ? <div className='department-grid'>{data.map(department => <DepartmentCard department={department} key={department.id} />)}</div> : null}
	</div>
}
