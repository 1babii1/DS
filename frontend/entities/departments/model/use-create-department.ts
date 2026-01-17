import { useMutation, useQueryClient } from '@tanstack/react-query'
import { departmentsApi } from '../api/departments.api'
import { isEnvelopeError } from '@/shared/api/errors'
import { toast } from 'sonner'

export function useCreateDepartment() {
	const queryClient = useQueryClient()

	const mutation = useMutation({
		mutationFn: departmentsApi.CreateDepartment,
		onSettled: () => {
			queryClient.invalidateQueries({ queryKey: ['departments'] })
		},
		onError: error => {
			if (isEnvelopeError(error)) {
				toast.error(error.message)
			} else {
				toast.error('Failed to create department')
			}
		},
		onSuccess: () => {
			toast.success('Department created successfully')
		}
	})

	return {
		createDepartment: mutation.mutate,
		isError: mutation.isError,
		error: mutation.error,
		isPending: mutation.isPending
	}
}
