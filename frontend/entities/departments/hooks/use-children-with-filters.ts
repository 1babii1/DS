import { useQuery } from '@tanstack/react-query'
import { ParentDepartment, PoginationResponse } from '../types/department.types'
import { departmentsApi } from '../api/departments.api'

interface Props {
	departmentId: string
	showChildren: boolean
	page?: number
	size?: number
}

export function useChildrenWithFilters({
	departmentId,
	showChildren,
	page,
	size
}: Props) {
	const {
		data: children,
		isLoading: isLoadingChildren,
		refetch
	} = useQuery<PoginationResponse<ParentDepartment>>({
		queryKey: ['children', departmentId],
		queryFn: () =>
			departmentsApi.getChildrenLazy(departmentId, { page, size }),
		enabled: showChildren,
		staleTime: 1000 * 60 * 5
	})
	return { children: children?.items, isLoadingChildren, refetch }
}
