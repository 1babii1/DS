import { Button } from '@/shared/components/ui/button'
import {
	Field,
	FieldError,
	FieldGroup,
	FieldLabel
} from '@/shared/components/ui/field'
import React, { useEffect, useState } from 'react'
import { useForm } from '@tanstack/react-form'
import { useLocationFilters } from '../stores/location-filters'
import { Label } from '@/shared/components/ui/label'
import { Input } from '@/shared/components/ui/input'
import {
	Select,
	SelectContent,
	SelectGroup,
	SelectItem,
	SelectTrigger,
	SelectValue
} from '@/shared/components/ui/select'
import { DepartmentFetchForm } from '@/entities/departments/types/department.types'
import { SearchPopover } from '@/features/departments/ui/search-popover'
import { useDepartmentSearch } from '@/entities/departments/hooks/use-department-search'

type FormValues = {
	departmentId: string[]
	isActive: string
	search: string
	pageSize: string
	sortBy: string
	sortDirection: string
}

export default function LocationFilters(): React.JSX.Element {
	const {
		departmentId,
		isActive,
		search,
		pageSize,
		sortBy,
		sortDirection,
		setFilters,
		resetFilters
	} = useLocationFilters()

	// 🔍 Поиск департаментов
	const [departmentSearch, setDepartmentSearch] = useState('')
	const { departments, isDepartmentsFetching } = useDepartmentSearch({
		departmentSearch,
		page: 1,
		size: 20
	})
	console.log(isActive, `isActive in LocationFilters ${typeof isActive}`)

	const form = useForm({
		defaultValues: {
			departmentId: departmentId,
			isActive: isActive,
			search: search,
			pageSize: pageSize.toString(),
			sortBy: sortBy,
			sortDirection: sortDirection
		} as FormValues,
		onSubmit: ({ value }) => {
			setFilters({
				departmentId: value.departmentId,
				isActive: value.isActive === 'all' ? '' : value.isActive,
				search: value.search,
				pageSize: value.pageSize,
				sortBy: value.sortBy,
				sortDirection: value.sortDirection
			})
		}
	})

	return (
		<div className='w-full p-6 bg-card border rounded-xl shadow-sm'>
			<form
				onSubmit={e => {
					e.preventDefault()
					form.handleSubmit()
				}}
				className='space-y-4 w-full'
			>
				<FieldGroup className='grid grid-cols-1 sm:grid-cols-2 md:grid-cols-3 lg:grid-cols-4 xl:grid-cols-5 gap-4 w-full'>
					<form.Field name='search'>
						{field => {
							const isInvalid =
								field.state.meta.isTouched &&
								!field.state.meta.isValid
							return (
								<Field>
									<FieldLabel className='flex flex-col'>
										<Label>Поиск</Label>
										<Input
											value={field.state.value}
											onChange={e =>
												field.handleChange(
													e.target.value
												)
											}
										/>
									</FieldLabel>
									{isInvalid && (
										<FieldError
											errors={field.state.meta.errors}
										/>
									)}
								</Field>
							)
						}}
					</form.Field>
					<form.Field name='departmentId'>
						{field => {
							const selectedValues = Array.isArray(
								field.state.value
							)
								? field.state.value
								: []
							return (
								<Field>
									<FieldLabel className='flex flex-col'>
										<Label>Подразделение</Label>
										<SearchPopover
											isLoading={false}
											isFetching={isDepartmentsFetching}
											items={departments}
											searchValue={departmentSearch}
											onSearchChange={setDepartmentSearch}
											placeholder='Search departments...'
											trigger={
												<Button
													variant='outline'
													className='w-full justify-between h-11'
												>
													{selectedValues.length > 0
														? `${selectedValues.length} selected`
														: 'Type to search locations...'}
												</Button>
											}
											onSelect={(
												department: DepartmentFetchForm
											) => {
												const newValue =
													selectedValues.includes(
														department.id
													)
														? selectedValues.filter(
																v =>
																	v !==
																	department.id
															)
														: [
																...selectedValues,
																department.id
															]
												field.handleChange(newValue)
											}}
											renderItem={(
												department: DepartmentFetchForm
											) => (
												<>
													<input
														type='checkbox'
														checked={selectedValues.includes(
															department.id
														)}
														className='mr-2 h-4 w-4'
														readOnly
													/>
													{department.name}
												</>
											)}
											getItemId={(
												department: DepartmentFetchForm
											) => department.id}
											emptyMessage='No departments found.'
										/>
									</FieldLabel>
									{field.state.meta.isTouched &&
										!field.state.meta.isValid && (
											<FieldError
												errors={field.state.meta.errors}
											/>
										)}
								</Field>
							)
						}}
					</form.Field>
					<form.Field name='isActive'>
						{field => {
							return (
								<Field>
									<FieldLabel>
										<Label>Активность</Label>
									</FieldLabel>
									<Select
										value={field.state.value}
										onValueChange={field.handleChange}
									>
										<SelectTrigger>
											<SelectValue placeholder='Выберите активность' />
										</SelectTrigger>
										<SelectContent>
											<SelectGroup>
												<SelectItem value='all'>
													Все
												</SelectItem>
												<SelectItem value='true'>
													Активно
												</SelectItem>
												<SelectItem value='false'>
													Неактивно
												</SelectItem>
											</SelectGroup>
										</SelectContent>
									</Select>
								</Field>
							)
						}}
					</form.Field>
					<form.Field name='sortBy'>
						{field => {
							return (
								<Field>
									<FieldLabel>
										<Label>Сортировка</Label>
									</FieldLabel>
									<Select
										value={field.state.value}
										onValueChange={field.handleChange}
									>
										<SelectTrigger>
											<SelectValue placeholder='Выберите сортировку' />
										</SelectTrigger>
										<SelectContent>
											<SelectGroup>
												<SelectItem value='name'>
													Name
												</SelectItem>
												<SelectItem value='city'>
													City
												</SelectItem>
												<SelectItem value='created_at'>
													Created at
												</SelectItem>
												<SelectItem value='updated_at'>
													Updated at
												</SelectItem>
											</SelectGroup>
										</SelectContent>
									</Select>
								</Field>
							)
						}}
					</form.Field>
					<form.Field name='sortDirection'>
						{field => {
							return (
								<Field>
									<FieldLabel>
										<Label>Сортировка по умолчанию</Label>
									</FieldLabel>
									<Select
										value={field.state.value}
										onValueChange={field.handleChange}
									>
										<SelectTrigger>
											<SelectValue placeholder='Выберите сортировку' />
										</SelectTrigger>
										<SelectContent>
											<SelectGroup>
												<SelectItem value='ASC'>
													ASC
												</SelectItem>
												<SelectItem value='DESC'>
													DESC
												</SelectItem>
											</SelectGroup>
										</SelectContent>
									</Select>
								</Field>
							)
						}}
					</form.Field>
					<form.Field name='pageSize'>
						{field => {
							return (
								<Field>
									<FieldLabel>
										<Label>Количество записей</Label>
									</FieldLabel>
									<Select
										value={field.state.value}
										onValueChange={field.handleChange}
									>
										<SelectTrigger>
											<SelectValue placeholder='Выберите количество записей' />
										</SelectTrigger>
										<SelectContent>
											<SelectGroup>
												<SelectItem value='2'>
													2
												</SelectItem>
												<SelectItem value='4'>
													4
												</SelectItem>
												<SelectItem value='6'>
													6
												</SelectItem>
												<SelectItem value='8'>
													8
												</SelectItem>
											</SelectGroup>
										</SelectContent>
									</Select>
								</Field>
							)
						}}
					</form.Field>
				</FieldGroup>

				<div className='flex gap-2'>
					<Button type='submit'>Применить</Button>
					<Button
						type='button'
						variant='outline'
						onClick={resetFilters}
					>
						Сбросить
					</Button>
				</div>
			</form>
		</div>
	)
}
