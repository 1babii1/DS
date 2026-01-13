import React from 'react'
import {
	Card,
	CardContent,
	CardDescription,
	CardFooter,
	CardHeader,
	CardTitle
} from '@/components/ui/card'
import { ParentDepartment } from '@/entities/departments/types/department.types'
import { Button } from '@/components/ui/button'
import Link from 'next/link'

type DepartmentCardProps = {
	department: ParentDepartment
}

export default function DepartmentCard({
	department
}: DepartmentCardProps): React.JSX.Element {
	return (
		<div className='h-64 w-full'>
			<Card className='flex h-full flex-col'>
				<CardHeader>
					<CardTitle className='line-clamp-2'>
						<div className='flex flex-row justify-between'>
							{department.name}
							<Button>
								<Link href={`/departments/${department.id}`}>
									Просмотр
								</Link>
							</Button>
						</div>
					</CardTitle>

					<CardDescription>
						Статус: {department.isActive ? 'Активен' : 'Неактивен'}
					</CardDescription>
				</CardHeader>
				<CardContent className='flex-1 overflow-y-auto'>
					<div className='space-y-2 text-sm'>
						<p>
							<span className='font-semibold'>Родитель: </span>
							{department.parentId
								? department.parentId
								: 'Корневой департамент'}
						</p>
						<p>
							<span className='font-semibold'>Путь: </span>
							{department.path}
						</p>
					</div>
				</CardContent>
				<CardFooter className='border-t pt-4'>
					<p className='text-xs text-muted-foreground'>
						Обновлено {department.updatedAt.toLocaleString()}
					</p>
				</CardFooter>
			</Card>
		</div>
	)
}
