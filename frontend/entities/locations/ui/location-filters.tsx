import { Button } from '@/shared/components/ui/button'
import React from 'react'
import { useLocationFilters } from '../stores/location-filters'
import { Input } from '@/shared/components/ui/input'
import {
	Select,
	SelectContent,
	SelectItem,
	SelectTrigger,
	SelectValue
} from '@/shared/components/ui/select'
import { DepartmentFetchForm } from '@/entities/departments/types/department.types'
import { SearchPopover } from '@/features/departments/ui/search-popover'
import { useDepartmentSearch } from '@/entities/departments/hooks/use-department-search'
import { useLocationsFilters } from '../hooks/use-location-filters'

export default function LocationFilters(): React.JSX.Element {
	const {
		departmentId,
		departmentSearch,
		isActive,
		search,
		size,
		sortBy,
		sortDirection,
		setFilters,
		resetFilters
	} = useLocationFilters()
	const { refetch } = useLocationsFilters()

	// 🔍 Поиск департаментов
	const { departments, isDepartmentsFetching } = useDepartmentSearch({
		departmentSearch: departmentSearch,
		page: 1,
		size: 20
	})
	const handleDepartmentSelect = (department: DepartmentFetchForm) => {
		const newValue = departmentId.includes(department.id)
			? departmentId.filter(v => v !== department.id)
			: [...departmentId, department.id]
		setFilters({ departmentId: newValue })
	}

	return (
		<div className='w-full p-6 bg-card border rounded-xl shadow-sm'>
			<div className='grid grid-cols-1 sm:grid-cols-2 md:grid-cols-3 gap-4'>
				<div>
					<h6>Поиск</h6>
					<Input
						value={search}
						onChange={e => setFilters({ search: e.target.value })}
						placeholder='Поиск...'
					/>
				</div>

				<div>
					<h6>Активность</h6>
					<Select
						value={isActive}
						onValueChange={v =>
							setFilters({ isActive: v === 'all' ? '' : v })
						}
					>
						<SelectTrigger>
							<SelectValue placeholder='Активность' />
						</SelectTrigger>
						<SelectContent>
							<SelectItem value='all'>Все</SelectItem>
							<SelectItem value='true'>Активно</SelectItem>
							<SelectItem value='false'>Неактивно</SelectItem>
						</SelectContent>
					</Select>
				</div>

				<div>
					<h6>Подразделения</h6>
					<SearchPopover
						isLoading={false}
						isFetching={isDepartmentsFetching}
						items={departments}
						searchValue={departmentSearch} // ✅ Сохранено в localStorage
						onSearchChange={value =>
							setFilters({ departmentSearch: value })
						} // 🔥 Сохраняется!
						placeholder='Подразделения...'
						trigger={
							<Button
								variant='outline'
								className='w-full justify-between h-11'
							>
								{departmentId.length > 0
									? `${departmentId.length} выбрано`
									: 'Подразделения...'}
							</Button>
						}
						onSelect={handleDepartmentSelect}
						renderItem={department => (
							<div className='flex items-center p-2'>
								<input
									type='checkbox'
									checked={departmentId.includes(
										department.id
									)}
									className='mr-2 h-4 w-4 rounded'
									readOnly
								/>
								{department.name}
							</div>
						)}
						getItemId={department => department.id}
						emptyMessage='Введите название подразделения'
					/>
				</div>
				<div>
					<h6>Размер страницы</h6>
					<Select
						value={size}
						onValueChange={v => setFilters({ size: v })}
					>
						<SelectTrigger>
							<SelectValue placeholder='Количество записей' />
						</SelectTrigger>
						<SelectContent>
							<SelectItem value='2'>2</SelectItem>
							<SelectItem value='4'>4</SelectItem>
							<SelectItem value='6'>6</SelectItem>
						</SelectContent>
					</Select>
				</div>

				<div>
					<h6>Сортировка</h6>
					<Select
						value={sortBy}
						onValueChange={v => setFilters({ sortBy: v })}
					>
						<SelectTrigger>
							<SelectValue placeholder='Сортировка по' />
						</SelectTrigger>
						<SelectContent>
							<SelectItem value='name'>Name</SelectItem>
							<SelectItem value='city'>City</SelectItem>
							<SelectItem value='created_at'>
								Created at
							</SelectItem>
							<SelectItem value='updated_at'>
								Updated at
							</SelectItem>
						</SelectContent>
					</Select>
				</div>

				<div>
					<h6>Направление сортировки</h6>
					<Select
						value={sortDirection}
						onValueChange={v => setFilters({ sortDirection: v })}
					>
						<SelectTrigger>
							<SelectValue placeholder='Направление сортировки' />
						</SelectTrigger>
						<SelectContent>
							<SelectItem value='ASC'>ASC</SelectItem>
							<SelectItem value='DESC'>DESC</SelectItem>
						</SelectContent>
					</Select>
				</div>
			</div>

			<div className='flex gap-2 mt-4'>
				<Button onClick={() => refetch()}>Применить</Button>
				<Button onClick={() => resetFilters()}>Сбросить</Button>
			</div>
		</div>
	)
}
