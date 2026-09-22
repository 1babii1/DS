'use client'

import { createContext, useContext } from 'react'
import type { ReactNode } from 'react'

const EditingCapabilityContext = createContext(false)

export function EditingCapabilityProvider({ canEdit, children }: { canEdit: boolean; children: ReactNode }) {
	return <EditingCapabilityContext.Provider value={canEdit}>{children}</EditingCapabilityContext.Provider>
}

export function useCanEdit() {
	return useContext(EditingCapabilityContext)
}
