import { useMutation, useQueryClient } from '@tanstack/react-query'
import { departmentsApi } from '../api/departments.api'
import { isEnvelopeError } from '@/shared/api/errors'
import { toast } from 'sonner'
import { UpdateParentRequest } from '../types/department.types'

export function UseUpdateParent({
	departmentId,
	parentDepartmentId
}: UpdateParentRequest) {
	const queryClient = useQueryClient()

	const mutation = useMutation({
		mutationFn: () =>
			departmentsApi.UpdateParent({ departmentId, parentDepartmentId }),
		onSettled: () => {
			queryClient.invalidateQueries({ queryKey: ['departments'] })
		},
		onError: error => {
			if (isEnvelopeError(error)) {
				toast.error(error.message)
			} else {
				toast.error('Failed to update parent')
			}
		},
		onSuccess: () => {
			toast.success('Parent updated successfully')
		}
	})
	return {
		updateParent: mutation.mutate,
		isError: mutation.isError,
		error: mutation.error,
		isPending: mutation.isPending
	}
}
