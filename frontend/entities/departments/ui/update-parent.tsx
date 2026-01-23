import { Button } from '@/shared/components/ui/button'
import {
	Dialog,
	DialogContent,
	DialogDescription,
	DialogHeader,
	DialogTitle,
	DialogTrigger
} from '@/shared/components/ui/dialog'

import React from 'react'
import UpdateParentForm from './update-parent-form'

export default function UpdateParent({
	currentDepartmentId
}: {
	currentDepartmentId: string
}): React.JSX.Element {
	return (
		<div>
			<Dialog>
				<DialogTrigger asChild>
					<Button variant='outline'>Update parent</Button>
				</DialogTrigger>
				<DialogContent className='sm:max-w-106.25'>
					<DialogHeader>
						<DialogTitle>Update parent</DialogTitle>
						<DialogDescription>
							Enter the departments name and other details
						</DialogDescription>
						<UpdateParentForm
							currentDepartmentId={currentDepartmentId}
						/>
					</DialogHeader>
				</DialogContent>
			</Dialog>
		</div>
	)
}
