'use client'

import { useQuery } from '@tanstack/react-query'
import { createContext, useContext } from 'react'
import type { ReactNode } from 'react'

const EditingCapabilityContext = createContext(false)
const capabilityQueryKey = ['session', 'capabilities'] as const

async function getCapabilities(): Promise<{ canEdit: boolean }> {
	const response = await fetch('/api/session/capabilities', { cache: 'no-store' })
	if (!response.ok) return { canEdit: false }
	const value: unknown = await response.json()
	return value && typeof value === 'object' && typeof (value as { canEdit?: unknown }).canEdit === 'boolean' ? { canEdit: (value as { canEdit: boolean }).canEdit } : { canEdit: false }
}

export function EditingCapabilityProvider({ authenticated, children }: { authenticated: boolean; children: ReactNode }) {
	const capability = useQuery({ queryKey: capabilityQueryKey, queryFn: getCapabilities, enabled: authenticated, staleTime: 60_000, refetchOnWindowFocus: true, retry: false })
	return <EditingCapabilityContext.Provider value={capability.data?.canEdit ?? false}>{children}</EditingCapabilityContext.Provider>
}

export function useCanEdit() {
	return useContext(EditingCapabilityContext)
}
