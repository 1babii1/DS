import { generateKeyTSQueryDepartment } from '@/shared/cache/generate-key'
import { ParentDepartment } from '../types/department.types'
import { departmentsApi } from '../api/departments.api'
import { useQuery } from '@tanstack/react-query'

export function useRootDepartments() {
	const { data, isLoading, isPending, refetch, error } = useQuery<
		ParentDepartment[]
	>({
		queryKey: [generateKeyTSQueryDepartment.Roots()],
		queryFn: () => departmentsApi.getParentDepartments(),
		staleTime: 1000 * 60 * 5
	})
	return {
		data,
		isLoading,
		isPending,
		refetch,
		error
	}
}
