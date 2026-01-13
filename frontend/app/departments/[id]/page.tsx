'use client'

import { departmentsApi } from '@/entities/departments/api/departments.api'
import React from 'react'
import { RefreshCcw } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { ParentDepartment } from '@/entities/departments/types/department.types'
import { useParams } from 'next/navigation'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { DepartmentChildren } from '@/widgets/departments/ui/department-children'

export default function DepartmentPage() {
	const params = useParams()
	const queryClientInstance = useQueryClient()
	const id = params?.id as string

	// Сначала проверяем есть ли в кэше список всех департаментов
	const cachedDepartments = queryClientInstance.getQueryData<
		ParentDepartment[]
	>(['roots'])
	console.log('cachedDepartments', cachedDepartments)
	const cachedDept = cachedDepartments?.find(d => d.id.toString() === id)

	// Если в кэше есть - используем его, если нет - запрашиваем с бэкэнда
	const {
		data: department,
		isLoading,
		error,
		refetch
	} = useQuery<ParentDepartment | null>({
		queryKey: ['department', id],
		queryFn: () => departmentsApi.getDepartment(id),
		initialData: cachedDept, // Используем данные из кэша если они есть
		staleTime: 1000 * 60 * 5 // 5 минут
	})

	if (isLoading && !cachedDept) {
		return <div className='p-6'>Загрузка...</div>
	}

	if (error || !department) {
		return (
			<div className='p-6'>
				<h1>Ошибка</h1>
				<p>{error?.message || 'Департамент не найден'}</p>
			</div>
		)
	}

	return (
		<div className='p-6'>
			<h1 className='text-4xl'>/{department.path}</h1>
			<div className='flex flex-row justify-between items-center mt-4'>
				<h1 className='text-4xl'>{department.name}</h1>
				<Button onClick={() => refetch()}>
					<RefreshCcw className='w-4 h-4' />
				</Button>
			</div>
			<div className='mt-6 space-y-4'>
				<p>
					<span className='font-semibold'>ID:</span> {department.id}
				</p>
				<p>
					<span className='font-semibold'>Статус:</span>{' '}
					{department.isActive ? 'Активен' : 'Неактивен'}
				</p>
				<p>
					<span className='font-semibold'>Путь:</span>{' '}
					{department.path}
				</p>
				<p>
					<span className='font-semibold'>Глубина:</span>{' '}
					{department.depth}
				</p>
				<p>
					<span className='font-semibold'>Обновлено:</span>{' '}
					{department.updatedAt.toLocaleString()}
				</p>

				<DepartmentChildren
					departmentId={id}
					hasMoreChildren={department.hasMoreChildren}
				/>
			</div>
		</div>
	)
}
