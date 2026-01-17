'use client'

import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { departmentsApi } from '@/entities/departments/api/departments.api'
import { ParentDepartment } from '@/entities/departments/types/department.types'
import { Button } from '@/shared/components/ui/button'
import DepartmentCard from '@/entities/departments/ui/department.card'

interface DepartmentChildrenProps {
	departmentId: string
	hasMoreChildren: boolean
}

export function DepartmentChildren({
	departmentId,
	hasMoreChildren
}: DepartmentChildrenProps) {
	const [showChildren, setShowChildren] = useState(false)

	const { data: children = [], isLoading: isLoadingChildren } = useQuery<
		ParentDepartment[]
	>({
		queryKey: ['children', departmentId],
		queryFn: () => departmentsApi.getChildrenLazy(departmentId),
		enabled: showChildren,
		staleTime: 1000 * 60 * 5
	})

	return (
		<div>
			<div className='flex flex-row justify-between items-center'>
				<span className='font-semibold'>Дочерние департаменты:</span>
				{hasMoreChildren ? (
					<Button onClick={() => setShowChildren(!showChildren)}>
						{showChildren ? 'Скрыть' : 'Показать'}
					</Button>
				) : (
					<p>Отсутствуют</p>
				)}
			</div>
			{isLoadingChildren && showChildren ? (
				<div className='mt-2'>Загрузка дочерних департаментов...</div>
			) : (
				<div className='grid grid-cols-1 gap-4 md:grid-cols-2 lg:grid-cols-3 mt-4'>
					{showChildren &&
						children?.map(child => (
							<DepartmentCard department={child} key={child.id} />
						))}
				</div>
			)}
		</div>
	)
}
