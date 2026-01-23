import { Button } from '@/shared/components/ui/button'
import {
	Dialog,
	DialogClose,
	DialogContent,
	DialogDescription,
	DialogFooter,
	DialogHeader,
	DialogTitle,
	DialogTrigger
} from '@/shared/components/ui/dialog'
import {
	Field,
	FieldError,
	FieldGroup,
	FieldLabel
} from '@/shared/components/ui/field'
import { Input } from '@/shared/components/ui/input'
import { Label } from '@/shared/components/ui/label'
import { cn } from '@/shared/lib/utils'
import { useForm } from '@tanstack/react-form'
import { Check, Loader2 } from 'lucide-react'
import { useState } from 'react'
import * as z from 'zod'
import { SearchPopover } from '../../../features/departments/ui/search-popover'
import { useCreateDepartment } from '../model/use-create-department'
import {
	DepartmentFetchForm,
	DepartmentForm,
	LocationForm
} from '../types/department.types'
import { useDepartmentSearch } from '../hooks/use-department-search'
import { useLocationSearch } from '@/entities/locations/hooks/use-location-search'

const formSchema = z.object({
	name: z
		.string()
		.min(3, 'Department name must be between 3 and 150 characters')
		.max(150, 'Department name must be between 3 and 150 characters')
		.trim()
		.regex(/^[^\s]*$/, 'Department name cannot contain only whitespace'),
	identifier: z
		.string()
		.min(3, 'Department identifier must be between 3 and 150 characters')
		.max(150, 'Department identifier must be between 3 and 150 characters')
		.regex(
			/^[a-zA-Z0-9]+$/,
			'Department identifier must contain only latin letters and numbers'
		)
		.trim(),
	parentDepartmentId: z.string().optional(),
	depth: z.number().min(0).optional(),
	locationsIds: z
		.array(z.string())
		.min(1, 'At least one location must be selected')
})

export function CreateDepartment({
	currentDepartmentId
}: {
	currentDepartmentId?: string
}) {
	const { createDepartment } = useCreateDepartment()

	const form = useForm({
		defaultValues: {
			name: '',
			identifier: '',
			parentDepartmentId: '',
			depth: 0,
			locationsIds: []
		} as DepartmentForm,
		validators: {
			onSubmit: formSchema
		},
		onSubmit: async ({ value }) => {
			createDepartment(value)
			form.reset()
		}
	})

	// 🔍 Поиск департаментов
	const [departmentSearch, setDepartmentSearch] = useState('')
	const { departments, isDepartmentsFetching } = useDepartmentSearch({
		currentDepartmentId,
		departmentSearch,
		page: 1,
		size: 20
	})

	// 🔍 Поиск локаций
	const [locationSearch, setLocationSearch] = useState('')
	const { locations, isLocationsFetching } = useLocationSearch({
		locationSearch,
		page: '1',
		size: '20'
	})

	return (
		<Dialog>
			<DialogTrigger asChild>
				<Button variant='outline'>Create</Button>
			</DialogTrigger>
			<DialogContent className='sm:max-w-106.25'>
				<DialogHeader>
					<DialogTitle>Create a new department</DialogTitle>
					<DialogDescription>
						Enter the departments name and other details
					</DialogDescription>
				</DialogHeader>
				<form
					onSubmit={async e => {
						e.preventDefault()
						await form.handleSubmit()
					}}
				>
					<FieldGroup>
						<form.Field name='name'>
							{field => {
								const isInvalid =
									field.state.meta.isTouched &&
									!field.state.meta.isValid
								return (
									<Field>
										<FieldLabel>
											<Label>Name</Label>
										</FieldLabel>
										<Input
											value={field.state.value}
											onBlur={field.handleBlur}
											onChange={e =>
												field.handleChange(
													e.target.value
												)
											}
											aria-invalid={isInvalid}
											placeholder='Department name'
										/>
										{isInvalid && (
											<FieldError
												errors={field.state.meta.errors}
											/>
										)}
									</Field>
								)
							}}
						</form.Field>

						<form.Field name='identifier'>
							{field => {
								const isInvalid =
									field.state.meta.isTouched &&
									!field.state.meta.isValid
								return (
									<Field>
										<FieldLabel>
											<Label>Identifier</Label>
										</FieldLabel>
										<Input
											value={field.state.value}
											onBlur={field.handleBlur}
											onChange={e =>
												field.handleChange(
													e.target.value
												)
											}
											aria-invalid={isInvalid}
											placeholder='Department identifier'
										/>
										{isInvalid && (
											<FieldError
												errors={field.state.meta.errors}
											/>
										)}
									</Field>
								)
							}}
						</form.Field>
						<form.Field name='parentDepartmentId'>
							{field => (
								<Field>
									<FieldLabel>
										<Label>Parent Department</Label>
									</FieldLabel>
									<SearchPopover
										isLoading={false}
										isFetching={isDepartmentsFetching}
										items={departments}
										searchValue={departmentSearch}
										onSearchChange={setDepartmentSearch}
										placeholder='Search departments...'
										isSingleSelect={true}
										trigger={
											<Button
												variant='outline'
												className='w-full justify-between'
											>
												{field.state.value
													? departments.find(
															d =>
																d.id ===
																field.state
																	.value
														)?.name ||
														field.state.value
													: 'Select department...'}
												{isDepartmentsFetching && (
													<Loader2 className='ml-2 h-4 w-4 animate-spin' />
												)}
											</Button>
										}
										onSelect={(dept: DepartmentFetchForm) =>
											field.handleChange(dept.id)
										}
										renderItem={(
											dept: DepartmentFetchForm
										) => (
											<>
												<Check
													className={cn(
														'mr-2 h-4 w-4',
														field.state.value ===
															dept.id
															? 'opacity-100'
															: 'opacity-0'
													)}
												/>
												{dept.name}
											</>
										)}
										getItemId={(
											dept: DepartmentFetchForm
										) => dept.id}
										emptyMessage='No departments found.'
									/>
								</Field>
							)}
						</form.Field>

						<form.Field name='depth'>
							{field => {
								const isInvalid =
									field.state.meta.isTouched &&
									!field.state.meta.isValid
								return (
									<Field>
										<FieldLabel>
											<Label>Depth</Label>
										</FieldLabel>
										<Input
											min={0}
											type='number'
											value={field.state.value ?? ''}
											onBlur={field.handleBlur}
											onChange={e => {
												const numValue =
													e.target.value === ''
														? 0
														: Number(e.target.value)
												field.handleChange(numValue)
											}}
											aria-invalid={isInvalid}
											placeholder='Depth'
										/>
										{isInvalid && (
											<FieldError
												errors={field.state.meta.errors}
											/>
										)}
									</Field>
								)
							}}
						</form.Field>

						<form.Field name='locationsIds'>
							{field => {
								const selectedValues = Array.isArray(
									field.state.value
								)
									? field.state.value
									: []
								return (
									<Field>
										<FieldLabel>
											<Label>Locations</Label>
										</FieldLabel>
										<SearchPopover
											isLoading={false}
											isFetching={isLocationsFetching}
											items={locations}
											searchValue={locationSearch}
											onSearchChange={setLocationSearch}
											placeholder='Search locations...'
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
												location: LocationForm
											) => {
												const newValue =
													selectedValues.includes(
														location.id
													)
														? selectedValues.filter(
																v =>
																	v !==
																	location.id
															)
														: [
																...selectedValues,
																location.id
															]
												field.handleChange(newValue)
											}}
											renderItem={(
												location: LocationForm
											) => (
												<>
													<input
														type='checkbox'
														checked={selectedValues.includes(
															location.id
														)}
														className='mr-2 h-4 w-4'
														readOnly
													/>
													{location.name}
												</>
											)}
											getItemId={(
												location: LocationForm
											) => location.id}
											emptyMessage='No locations found.'
										/>
										{field.state.meta.isTouched &&
											!field.state.meta.isValid && (
												<FieldError
													errors={
														field.state.meta.errors
													}
												/>
											)}
									</Field>
								)
							}}
						</form.Field>
					</FieldGroup>

					<DialogFooter>
						<DialogClose asChild>
							<Button variant='outline'>Cancel</Button>
						</DialogClose>
						<DialogClose asChild>
							<Button type='submit'>Save changes</Button>
						</DialogClose>
					</DialogFooter>
				</form>
			</DialogContent>
		</Dialog>
	)
}
