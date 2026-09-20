export type SearchKind = 'employee' | 'department' | 'position' | 'location' | 'audit'

export type SearchResult = {
	kind: SearchKind
	id: string
	title: string
	subtitle: string | null
	matchedFields: string[]
	rank: number
}

export type SearchResponse = {
	query: string
	results: SearchResult[]
}
