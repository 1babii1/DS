'use client'

import { RefCallback, useCallback, useState } from 'react'
import { Button } from '@/shared/components/ui/button'
import DepartmentCard from '@/entities/departments/ui/department-card'
import { RefreshCcw } from 'lucide-react'
import { useInfiniteChildren } from '../hooks/use-infinite-children'

interface DepartmentChildrenProps {
	departmentId: string
	hasMoreChildren: boolean
}

export function DepartmentChildren({
	departmentId,
	hasMoreChildren
}: DepartmentChildrenProps) {
	const [showChildren, setShowChildren] = useState(false)
	const [pageSize, setPageSize] = useState(20)

	const {
		children,
		isLoadingChildren,
		refetch,
		hasNextPage,
		isFetchingNextPage,
		fetchNextPage
	} = useInfiniteChildren({
		departmentId,
		showChildren,
		page: 1,
		pageSize
	})

	const cursorRef: RefCallback<HTMLDivElement> = useCallback(
		el => {
			const observer = new IntersectionObserver(
				entries => {
					if (
						entries[0].isIntersecting &&
						hasNextPage &&
						!isFetchingNextPage
					) {
						fetchNextPage()
					}
				},
				{ threshold: 1.0 }
			)
			if (el) {
				observer.observe(el)
			}
			return () => {
				observer.disconnect()
			}
		},
		[hasNextPage, isFetchingNextPage, fetchNextPage]
	)

	return (
		<div>
			<div className='flex flex-row justify-between items-center'>
				<span className='font-semibold'>Дочерние департаменты:</span>
				<select
					name='size'
					id='pageSize'
					value={pageSize}
					onChange={e => setPageSize(Number(e.target.value))}
				>
					<option value='2'>2</option>
					<option value='4'>4</option>
					<option value='6'>6</option>
					<option value='8'>8</option>
					<option value='20' selected>
						20
					</option>
				</select>
				{hasMoreChildren ? (
					<>
						<Button onClick={() => setShowChildren(!showChildren)}>
							{showChildren ? 'Скрыть' : 'Показать'}
						</Button>
						{showChildren && (
							<Button
								variant='ghost'
								size='sm'
								onClick={() => refetch()}
							>
								<RefreshCcw className='w-4 h-4 mr-2' />
								Обновить
							</Button>
						)}
					</>
				) : (
					<p>Отсутствуют</p>
				)}
			</div>
			{isLoadingChildren && showChildren ? (
				<div className='mt-2'>Загрузка дочерних департаментов...</div>
			) : (
				<div className='grid grid-cols-1 gap-4 mt-4'>
					{showChildren &&
						children?.map(child => (
							<DepartmentCard department={child} key={child.id} />
						))}
				</div>
			)}
			<div ref={cursorRef} />
			{isFetchingNextPage && <div>Загрузка...</div>}
		</div>
	)
}
