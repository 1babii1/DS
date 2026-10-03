'use client'

import { useQuery } from '@tanstack/react-query'

import { walletVersionFor } from '@/shared/api/wallet-version'
import { employeesApi } from './employees.api'

export const employeeCardKey = (employeeId: string) => ['employee-card', employeeId] as const

// How many times a card that came back behind is asked again, and how long to wait between asks. The backend already waits up to five seconds
// for the version we send; this covers the case where even that was not enough, without hammering a service that is catching up.
export const staleCardRetries = 5
export const staleCardRetryMs = 2_000

export function cardNeedsAnotherLook(data: { consistent: boolean } | undefined, updates: number): boolean {
	return data?.consistent === false && updates <= staleCardRetries
}

export function useEmployeeCard(employeeId: string) {
	return useQuery({
		queryKey: employeeCardKey(employeeId),
		queryFn: () => employeesApi.card(employeeId, walletVersionFor(employeeId)),
		enabled: Boolean(employeeId),
		refetchInterval: query => (cardNeedsAnotherLook(query.state.data, query.state.dataUpdateCount) ? staleCardRetryMs : false)
	})
}
