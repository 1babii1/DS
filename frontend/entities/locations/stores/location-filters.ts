import { create } from 'zustand'
import { persist } from 'zustand/middleware'

interface FiltersState {
	isActive: string
	departmentId: string[]
	departmentSearch: string
	search: string
	size: string
	sortBy: string
	sortDirection: string
	setFilters: (filters: Partial<FiltersState>) => void
	resetFilters: () => void
	_hasHydrated: boolean
	setHasHydrated: (bool: boolean) => void
}

export const useLocationFilters = create<FiltersState>()(
	persist(
		(set, get) => ({
			_hasHydrated: false,
			isActive: '',
			departmentId: [],
			departmentSearch: '',
			search: '',
			size: '20',
			sortBy: 'name',
			sortDirection: 'ASC',
			setFilters: filters => set(state => ({ ...state, ...filters })),
			resetFilters: () =>
				set({
					search: '',
					size: '20',
					departmentSearch: '',
					sortBy: 'name',
					sortDirection: 'ASC',
					isActive: undefined,
					departmentId: []
				}),
			setHasHydrated: (bool: boolean) => set({ _hasHydrated: bool })
		}),
		{
			name: 'department-filters',
			onRehydrateStorage: () => (state, error) => {
				if (!error) {
					state?.setHasHydrated(true) // Внутренний setter
				}
			}
		}
	)
)
