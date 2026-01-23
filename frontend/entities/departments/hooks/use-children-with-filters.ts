import { useQuery } from '@tanstack/react-query'
import { ParentDepartment, PoginationResponse } from '../types/department.types'
import { departmentsApi } from '../api/departments.api'
import { generateKeyTSQueryDepartment } from '@/shared/cache/generate-key'

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
		queryKey: [
			generateKeyTSQueryDepartment.Children(departmentId, page, size)
		],
		queryFn: () =>
			departmentsApi.getChildrenLazy(departmentId, { page, size }),
		enabled: showChildren,
		staleTime: 1000 * 60 * 5
	})
	return { children: children?.items, isLoadingChildren, refetch }
}
