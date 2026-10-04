'use client'

import { useQuery } from '@tanstack/react-query'

import { employeesApi } from './employees.api'

export function useEmployeeCard(employeeId: string | null, minimumWalletVersion?: number) {
	return useQuery({
		queryKey: ['employees', 'card', employeeId, minimumWalletVersion ?? null],
		queryFn: () => employeesApi.card(employeeId as string, minimumWalletVersion),
		enabled: Boolean(employeeId),
		refetchInterval: query => query.state.data?.consistent === false ? 2_000 : false,
		refetchIntervalInBackground: false
	})
}
