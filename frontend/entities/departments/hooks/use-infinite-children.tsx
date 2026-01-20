import { infiniteQueryOptions, useInfiniteQuery } from '@tanstack/react-query'
import { departmentsApi } from '../api/departments.api'

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
		queryKey: ['children'],
		queryFn: ({ pageParam }) => {
			return departmentsApi.getChildrenLazy(departmentId, {
				page: pageParam ?? page ?? 1,
				size: pageSize ?? 20
			})
		},
		enabled: showChildren,
		initialPageParam: 1,
		getNextPageParam: response => {
			if (response && response.nextPageExists) return response.page + 1
			return undefined
		},
		select: data => ({
			items: data.pages.flatMap(page => page.items),
			totalCount: data.pages[0]?.totalCount ?? 0,
			nextPageExists:
				data.pages[data.pages.length - 1]?.nextPageExists ?? false,
			page: data.pages
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
