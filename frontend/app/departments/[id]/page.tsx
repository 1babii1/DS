'use client'

import { ChevronLeft, RefreshCw } from 'lucide-react'
import Link from 'next/link'
import { useParams } from 'next/navigation'
import { useQuery, useQueryClient } from '@tanstack/react-query'

import { Button } from '@/shared/ui/button'
import { departmentsApi } from '@/entities/departments/api/departments.api'
import type { ParentDepartment } from '@/entities/departments/types/department.types'
import { getHttpStatus } from '@/shared/api/http-error'
import { DepartmentChildren } from '@/widgets/departments/ui/department-children'
import { AccessState } from '@/shared/ui/access-state'

export default function DepartmentPage() {
	const { id } = useParams<{ id: string }>()
	const queryClient = useQueryClient()
	const cachedDepartments = queryClient.getQueryData<ParentDepartment[]>(['departments', 'roots'])
	const cachedDepartment = cachedDepartments?.find(department => department.id.toString() === id)
	const { data: department, error, isPending, refetch } = useQuery<ParentDepartment | null>({ queryKey: ['departments', id], queryFn: () => departmentsApi.getDepartment(id), initialData: cachedDepartment, staleTime: 5 * 60 * 1000 })
	const accessStatus = getHttpStatus(error)

	if (isPending && !cachedDepartment) return <div className='page'><section aria-busy='true' className='empty-state'><h2>Loading department</h2><p>Retrieving the department from the directory service.</p></section></div>
	if (accessStatus === 401 || accessStatus === 403) return <div className='page'><AccessState resource='this department' status={accessStatus} /></div>
	if (error || !department) return <div className='page'><section className='empty-state' role='alert'><h2>Department data is unavailable</h2><p>{error?.message ?? 'The requested department could not be found.'}</p><Link className='retry-button' href='/departments'><ChevronLeft aria-hidden='true' size={16} />Back to organization</Link></section></div>

	return <div className='page'><Link className='back-link' href='/departments'><ChevronLeft aria-hidden='true' size={16} />Organization</Link><header className='page-heading page-heading--detail'><div><p className='eyebrow'>Department · level {department.depth}</p><h1>{department.name}</h1></div><Button aria-label='Refresh department' className='refresh-button' onClick={() => refetch()} size='icon' variant='outline'><RefreshCw aria-hidden='true' size={16} /></Button></header><section className='detail-grid' aria-label='Department details'><div><span>Path</span><strong>/{department.path}</strong></div><div><span>Status</span><strong>{department.isActive ? 'Active' : 'Inactive'}</strong></div><div><span>Last updated</span><strong>{new Intl.DateTimeFormat('en', { dateStyle: 'medium', timeStyle: 'short' }).format(new Date(department.updatedAt))}</strong></div></section><section className='children-section'><DepartmentChildren departmentId={id} hasMoreChildren={department.hasMoreChildren} /></section></div>
}
