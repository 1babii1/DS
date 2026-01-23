'use client'

import { useRootDepartments } from '@/entities/departments/hooks/use-root-departments'
import { CreateDepartment } from '@/entities/departments/ui/create-department-dialog'
import DepartmentCard from '@/entities/departments/ui/department-card'
import React from 'react'

export default function DepartmentsPage(): React.JSX.Element {
	const { data, isLoading, isPending, error } = useRootDepartments()

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
			<CreateDepartment />
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
