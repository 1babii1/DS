import { Button } from '@/shared/components/ui/button'
import {
	DialogHeader,
	DialogFooter,
	Dialog,
	DialogTrigger,
	DialogClose,
	DialogContent,
	DialogDescription,
	DialogTitle
} from '@/shared/components/ui/dialog'

import React from 'react'
import { departmentsApi } from '../../../entities/departments/api/departments.api'

interface ConfirmDeleteProps {
	name: string
	id: string
}

export default function ConfirmDelete({
	name,
	id
}: ConfirmDeleteProps): React.JSX.Element {
	const deleteHandler = () => {
		departmentsApi.DeleteDepartment(id)
	}

	return (
		<Dialog>
			<DialogTrigger asChild>
				<Button variant='outline'>Delete</Button>
			</DialogTrigger>
			<DialogContent className='sm:max-w-106.25'>
				<DialogHeader>
					<DialogTitle>Delete department {name}</DialogTitle>
					<DialogDescription>
						Are you sure you want to delete this department?
					</DialogDescription>
				</DialogHeader>
				<DialogFooter>
					<DialogClose asChild>
						<Button variant='outline'>Cancel</Button>
					</DialogClose>
					<DialogClose asChild>
						<Button onClick={() => deleteHandler()}>Confirm</Button>
					</DialogClose>
				</DialogFooter>
			</DialogContent>
		</Dialog>
	)
}
