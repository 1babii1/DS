import { useQuery } from '@tanstack/react-query'
import { DepartmentFetchForm } from '../types/department.types'
import { departmentsApi } from '../api/departments.api'
import { useEffect, useState } from 'react'
import { generateKeyTSQueryDepartment } from '@/shared/cache/generate-key'

interface Props {
	departmentSearch: string
	currentDepartmentId?: string | null
	page?: number
	size?: number
	debounceMs?: number
}

export function useDepartmentSearch({
	currentDepartmentId,
	departmentSearch,
	page,
	size,
	debounceMs = 300
}: Props) {
	const [debounceSearch, setDebounceSearch] = useState(departmentSearch)

	useEffect(() => {
		const timer = setTimeout(() => {
			setDebounceSearch(departmentSearch)
		}, debounceMs)
		return () => clearTimeout(timer)
	}, [departmentSearch, debounceMs])

	const {
		data: departments = [] as DepartmentFetchForm[],
		isFetching: isDepartmentsFetching
	} = useQuery({
		queryKey: [generateKeyTSQueryDepartment.BySearch(debounceSearch)],
		queryFn: () =>
			departmentsApi.GetDepartmentBySearch({
				search: debounceSearch,
				page: page ?? 1,
				size: size ?? 20
			}),
		enabled: !!currentDepartmentId || !!debounceSearch,
		staleTime: 0
	})
	return {
		departments,
		isDepartmentsFetching
	}
}
