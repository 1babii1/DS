import { useInfiniteQuery } from '@tanstack/react-query'
import { departmentsApi, departmentsOptions } from '../api/departments.api'
import { generateKeyTSQueryDepartment } from '@/shared/cache/generate-key'

export function useInfiniteChildren({
	pageSize,
	departmentId,
	showChildren,
	page
}: {
	pageSize: number
	departmentId: string
	showChildren: boolean
	page?: number
}) {
	const {
		data,
		isLoading,
		isFetchingNextPage,
		fetchNextPage,
		refetch,
		hasNextPage
	} = useInfiniteQuery({
		...departmentsOptions.getChildrenLazyInfiniteOptions({
			pageSize,
			departmentId,
			showChildren,
			page
		})
	})

	return {
		isLoadingChildren: isLoading,
		isFetchingNextPage,
		fetchNextPage,
		refetch,
		hasNextPage,
		children: data?.items
	}
}
