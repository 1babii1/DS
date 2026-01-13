'use client'

import { departmentsApi } from '@/entities/departments/api/departments.api'
import { ParentDepartment } from '@/entities/departments/types/department.types'
import DepartmentCard from '@/entities/departments/ui/department.card'
import { useQuery } from '@tanstack/react-query'
import React from 'react'

export default function DepartmentsPage(): React.JSX.Element {
	const { data, isLoading, isPending, error } = useQuery<ParentDepartment[]>({
		queryFn: () => departmentsApi.getParentDepartments(),
		queryKey: ['roots']
	})

	if (isLoading) {
		return (
			<div>
				<h1>Departments</h1>Loading...
			</div>
		)
	}

	if (isPending) {
		return (
			<div>
				<h1>Departments</h1>Loading...
			</div>
		)
	}
	return (
		<div className='flex flex-col gap-5 max-w-7xl mx-auto'>
			<h1>Departments</h1>
			{error ? (
				'Ошибка при получении департаментов'
			) : (
				<div className='grid grid-cols-1 gap-4 md:grid-cols-2 lg:grid-cols-3 '>
					{data?.map(department => (
						<DepartmentCard
							department={department}
							key={department.id}
						/>
					))}
				</div>
			)}
		</div>
	)
}
