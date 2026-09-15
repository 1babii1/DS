'use client'

import { Search, X } from 'lucide-react'
import { usePathname, useRouter, useSearchParams } from 'next/navigation'
import { useEffect, useState } from 'react'

function CatalogFilterControls() {
	const router = useRouter()
	const pathname = usePathname()
	const params = useSearchParams()
	const searchParameter = params.get('search') ?? ''
	const [search, setSearch] = useState(searchParameter)
	const active = params.get('status') ?? 'active'
	const hasFilters = Boolean(search || active !== 'active')

	useEffect(() => {
		if (search === searchParameter) return

		const timeout = window.setTimeout(() => {
			const next = new URLSearchParams(params)
			if (search.trim()) next.set('search', search.trim())
			else next.delete('search')
			next.delete('page')
			router.replace(`${pathname}${next.size ? `?${next}` : ''}`)
		}, 250)
		return () => window.clearTimeout(timeout)
	}, [search, searchParameter, params, pathname, router])

	return <div className='catalog-filter'>
		<label><Search aria-hidden='true' size={16} /><input aria-label='Search catalogue' onChange={event => setSearch(event.target.value)} placeholder='Search catalogue' value={search} /></label>
		<select aria-label='Filter status' onChange={event => { const next = new URLSearchParams(params); next.set('status', event.target.value); next.delete('page'); router.replace(`${pathname}?${next}`) }} value={active}><option value='active'>Active</option><option value='inactive'>Inactive</option><option value='all'>All statuses</option></select>
		{hasFilters ? <button className='filter-reset' onClick={() => { setSearch(''); router.replace(pathname) }} type='button'><X aria-hidden='true' size={14} />Clear filters</button> : null}
	</div>
}

export function CatalogFilter() {
	const params = useSearchParams()
	const search = params.get('search') ?? ''
	const status = params.get('status') ?? 'active'

	return <CatalogFilterControls key={`${search}:${status}`} />
}
