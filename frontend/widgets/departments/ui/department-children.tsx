'use client'

import { ChevronDown, ChevronUp } from 'lucide-react'
import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'

import { departmentsApi } from '@/entities/departments/api/departments.api'
import type { ParentDepartment } from '@/entities/departments/types/department.types'
import DepartmentCard from '@/entities/departments/ui/department.card'

export function DepartmentChildren({ departmentId, hasMoreChildren }: { departmentId: string; hasMoreChildren: boolean }) {
	const [showChildren, setShowChildren] = useState(false)
	const { data: children = [], isPending } = useQuery<ParentDepartment[]>({
		queryKey: ['departments', departmentId, 'children'],
		queryFn: () => departmentsApi.getChildrenLazy(departmentId),
		enabled: showChildren,
		staleTime: 5 * 60 * 1000
	})

	if (!hasMoreChildren) return <section className='empty-state'><h2>No child departments</h2><p>This department does not currently have child departments in the directory.</p></section>
	return <section aria-labelledby='children-heading'>
		<div className='children-heading'><div><p className='eyebrow'>Organization structure</p><h2 id='children-heading'>Child departments</h2></div><button aria-expanded={showChildren} className='retry-button' onClick={() => setShowChildren(value => !value)} type='button'>{showChildren ? <ChevronUp aria-hidden='true' size={16} /> : <ChevronDown aria-hidden='true' size={16} />}{showChildren ? 'Hide departments' : 'Show departments'}</button></div>
		{showChildren && isPending ? <p className='loading-note' aria-live='polite'>Loading child departments…</p> : null}
		{showChildren && !isPending ? <div className='department-grid'>{children.map(child => <DepartmentCard department={child} key={child.id} />)}</div> : null}
	</section>
}
