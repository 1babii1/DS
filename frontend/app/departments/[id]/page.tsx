'use client'

import { departmentsApi } from '@/entities/departments/api/departments.api'
import React from 'react'
import { ArrowLeft, RefreshCcw } from 'lucide-react'
import { Button } from '@/shared/components/ui/button'
import { ParentDepartment } from '@/entities/departments/types/department.types'
import { useParams } from 'next/navigation'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { DepartmentChildren } from '@/entities/departments/ui/department-children'
import ConfirmDelete from '@/features/departments/ui/confirm-delete'
import Link from 'next/link'
import UpdateParent from '@/entities/departments/ui/update-parent'
import { generateKeyTSQueryDepartment } from '@/shared/cache/generate-key'

export default function DepartmentPage() {
	const params = useParams()
	const queryClientInstance = useQueryClient()
	const id = params?.id as string

	// Сначала проверяем есть ли в кэше список всех департаментов
	const cachedDepartments = queryClientInstance.getQueryData<
		ParentDepartment[]
	>(['roots'])

	const cachedDept = cachedDepartments?.find(d => d.id.toString() === id)

	// Если в кэше есть - используем его, если нет - запрашиваем с бэкэнда
	const {
		data: department,
		isLoading,
		error,
		refetch
	} = useQuery<ParentDepartment | null>({
		queryKey: [generateKeyTSQueryDepartment.ById(id)],
		queryFn: () => departmentsApi.getDepartment(id),
		initialData: cachedDept, // Используем данные из кэша если они есть
		staleTime: 1000 * 60 * 5 // 5 минут
	})

	console.log('department', department)

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
		<>
			<Link href='/departments'>
				<ArrowLeft className='h-4 w-4' />
			</Link>
			<div className='p-6 w-full'>
				<div className='flex flex-row justify-between items-center'>
					<h1 className='text-4xl'>/{department.path}</h1>
					<UpdateParent currentDepartmentId={department.id} />
					{department.isActive === true && (
						<ConfirmDelete
							name={department.name}
							id={department.id}
						/>
					)}
				</div>
				<div className='flex flex-row justify-between items-center mt-4'>
					<h1 className='text-4xl'>{department.name}</h1>
					<Button onClick={() => refetch()}>
						<RefreshCcw className='w-4 h-4' />
					</Button>
				</div>
				<div className='mt-6 space-y-4'>
					<p>
						<span className='font-semibold'>ID:</span>{' '}
						{department.id}
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
		</>
	)
}
