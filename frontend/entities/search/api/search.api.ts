import { axiosInstance } from '@/shared/api/axiosInstance'

import type { SearchResponse } from '../types/search.types'

type SearchEnvelope = { result: SearchResponse }

export const searchApi = {
	search: async (query: string, signal?: AbortSignal) => (await axiosInstance.get<SearchEnvelope>('/api/search', {
		params: { q: query, limit: 12 },
		signal
	})).data.result
}
