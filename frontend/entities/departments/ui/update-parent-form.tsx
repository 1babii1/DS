import { Field, FieldGroup, FieldLabel } from '@/shared/components/ui/field'
import { Label } from '@/shared/components/ui/label'
import { useForm } from '@tanstack/react-form'
import React, { useState } from 'react'
import { SearchPopover } from '../../../features/departments/ui/search-popover'
import { useDepartmentSearch } from '../hooks/use-department-search'
import * as z from 'zod'
import { Button } from '@/shared/components/ui/button'
import { Check, Loader2 } from 'lucide-react'
import {
	DepartmentFetchForm,
	UpdateParentRequest
} from '../types/department.types'
import { cn } from '@/shared/lib/utils'
import { UseUpdateParent } from '../model/use-update-parent'
import { DialogFooter } from '@/shared/components/ui/dialog'
import { DialogClose } from '@radix-ui/react-dialog'

const formSchema = z.object({
	departmentId: z.string(),
	parentDepartmentId: z.string()
})

export default function UpdateParentForm({
	currentDepartmentId
}: {
	currentDepartmentId: string
}): React.JSX.Element {
	const form = useForm({
		defaultValues: {
			departmentId: currentDepartmentId ?? '',
			parentDepartmentId: ''
		} as UpdateParentRequest,
		validators: {
			onSubmit: formSchema
		},
		onSubmit: async ({ value }) => {
			UseUpdateParent(value)
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

	return (
		<form>
			<FieldGroup>
				<form.Field name='parentDepartmentId'>
					{field => {
						return (
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
															field.state.value
													)?.name || field.state.value
												: 'Select department...'}
											{isDepartmentsFetching && (
												<Loader2 className='ml-2 h-4 w-4 animate-spin' />
											)}
										</Button>
									}
									onSelect={(dept: DepartmentFetchForm) =>
										field.handleChange(dept.id)
									}
									renderItem={(dept: DepartmentFetchForm) => (
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
									getItemId={(dept: DepartmentFetchForm) =>
										dept.id
									}
									emptyMessage='No departments found.'
								/>
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
	)
}
