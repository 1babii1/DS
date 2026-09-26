'use client'

import { useQuery } from '@tanstack/react-query'
import * as Dialog from '@radix-ui/react-dialog'
import { Activity, BriefcaseBusiness, Building2, Command, LayoutDashboard, MapPin, Search, UsersRound } from 'lucide-react'
import { useRouter } from 'next/navigation'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import type { ComponentType, KeyboardEvent as ReactKeyboardEvent } from 'react'

import { searchApi } from '@/entities/search/api/search.api'
import type { SearchKind, SearchResult } from '@/entities/search/types/search.types'
import { getHttpStatus } from '@/shared/api/http-error'
import { useDebouncedValue } from '@/shared/hooks/use-debounced-value'

type CommandItem = {
	description: string
	href: string
	id: string
	icon: ComponentType<{ size?: number; strokeWidth?: number }>
	kind?: SearchKind
	label: string
	matchedFields?: string[]
}

const navigationCommands: CommandItem[] = [
	{ id: 'navigate-overview', href: '/', label: 'Overview', description: 'Workspace overview', icon: LayoutDashboard },
	{ id: 'navigate-organization', href: '/departments', label: 'Organization', description: 'Department structure', icon: Building2 },
	{ id: 'navigate-people', href: '/people', label: 'People', description: 'Employee directory', icon: UsersRound },
	{ id: 'navigate-positions', href: '/positions', label: 'Positions', description: 'Role catalogue', icon: BriefcaseBusiness },
	{ id: 'navigate-locations', href: '/locations', label: 'Locations', description: 'Office footprint', icon: MapPin },
	{ id: 'navigate-activity', href: '/activity', label: 'Activity', description: 'Audit events', icon: Activity },
]

const resultIcons: Record<SearchKind, CommandItem['icon']> = {
	employee: UsersRound,
	department: Building2,
	position: BriefcaseBusiness,
	location: MapPin,
	audit: Activity
}

const searchKindLabels: Record<SearchKind, string> = {
	employee: 'People',
	department: 'Departments',
	position: 'Positions',
	location: 'Locations',
	audit: 'Activity'
}

function searchResultHref(result: SearchResult): string {
	switch (result.kind) {
		case 'department': return `/departments/${result.id}`
		case 'position': return `/positions?search=${encodeURIComponent(result.title)}`
		case 'location': return `/locations?search=${encodeURIComponent(result.title)}`
		case 'audit': return `/activity?aggregateId=${encodeURIComponent(result.id)}`
		case 'employee': return `/people?employeeId=${encodeURIComponent(result.id)}`
	}
}

function toSearchItem(result: SearchResult): CommandItem {
	return {
		id: `result-${result.kind}-${result.id}`,
		href: searchResultHref(result),
		label: result.title,
		description: result.subtitle ?? `${result.kind[0].toUpperCase()}${result.kind.slice(1)} result`,
		icon: resultIcons[result.kind],
		kind: result.kind,
		matchedFields: result.matchedFields
	}
}

export function CommandMenu() {
	const [open, setOpen] = useState(false)
	const [query, setQuery] = useState('')
	const [activeIndex, setActiveIndex] = useState(0)
	const input = useRef<HTMLInputElement>(null)
	const router = useRouter()
	const normalizedQuery = query.trim()
	const debouncedQuery = useDebouncedValue(normalizedQuery, 250)
	const hasSearchTerm = normalizedQuery.length >= 2
	const search = useQuery({
		queryKey: ['workspace-search', debouncedQuery],
		queryFn: ({ signal }) => searchApi.search(debouncedQuery, signal),
		enabled: hasSearchTerm && debouncedQuery.length >= 2,
		staleTime: 30 * 1000
	})
	const navigationMatches = useMemo(() => navigationCommands.filter(command => `${command.label} ${command.description}`.toLowerCase().includes(normalizedQuery.toLowerCase())), [normalizedQuery])
	const resultMatches = useMemo(() => hasSearchTerm && debouncedQuery === normalizedQuery ? (search.data?.results ?? []).map(toSearchItem) : [], [debouncedQuery, hasSearchTerm, normalizedQuery, search.data?.results])
	const groupedResults = useMemo(() => {
		return resultMatches.reduce<Partial<Record<SearchKind, CommandItem[]>>>((groups, item) => {
			const kind = item.kind
			if (!kind) return groups
			;(groups[kind] ??= []).push(item)
			return groups
		}, {})
	}, [resultMatches])
	const options = [...navigationMatches, ...resultMatches]
	const activeOptionIndex = Math.min(activeIndex, Math.max(options.length - 1, 0))
	const openMenu = useCallback(() => { setQuery(''); setActiveIndex(0); setOpen(true) }, [])
	const handleOpenChange = useCallback((nextOpen: boolean) => {
		if (nextOpen) {
			setQuery('')
			setActiveIndex(0)
		}
		setOpen(nextOpen)
	}, [])
	const select = useCallback((href: string) => { setOpen(false); router.push(href) }, [router])

	useEffect(() => {
		const onKeyDown = (event: KeyboardEvent) => {
			if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === 'k') {
				event.preventDefault()
				if (open) setOpen(false)
				else openMenu()
			}
			if (event.key === 'Escape') setOpen(false)
		}
		window.addEventListener('keydown', onKeyDown)
		return () => window.removeEventListener('keydown', onKeyDown)
	}, [open, openMenu])
	useEffect(() => { if (open) window.setTimeout(() => input.current?.focus(), 0) }, [open])
	const onSearchKeyDown = (event: ReactKeyboardEvent<HTMLInputElement>) => {
		if (event.key === 'ArrowDown' && options.length) { event.preventDefault(); setActiveIndex(index => Math.min(index + 1, options.length - 1)) }
		if (event.key === 'ArrowUp' && options.length) { event.preventDefault(); setActiveIndex(index => Math.max(index - 1, 0)) }
		if (event.key === 'Enter' && options[activeOptionIndex]) { event.preventDefault(); select(options[activeOptionIndex].href) }
	}

	const status = getHttpStatus(search.error)
	const showSearching = hasSearchTerm && (normalizedQuery !== debouncedQuery || search.isFetching)
	const showNoResults = hasSearchTerm && !showSearching && !search.error && resultMatches.length === 0 && navigationMatches.length === 0

	return <Dialog.Root onOpenChange={handleOpenChange} open={open}>
		<Dialog.Trigger asChild><button aria-label='Search workspace' className='command-trigger' type='button'><Search aria-hidden='true' size={15} /><span>Search workspace</span><kbd><Command size={11} />K</kbd></button></Dialog.Trigger>
		<Dialog.Portal>
			<Dialog.Overlay className='command-overlay' />
			<Dialog.Content aria-describedby='command-description' className='command-menu'>
				<Dialog.Title className='sr-only'>Search workspace</Dialog.Title>
				<Dialog.Description className='sr-only' id='command-description'>Search people, organization records, locations, positions, and audit activity, or open a workspace section.</Dialog.Description>
				<div className='command-search'><Search aria-hidden='true' size={16} /><input aria-activedescendant={options[activeOptionIndex]?.id} aria-autocomplete='list' aria-controls='command-results' aria-expanded={open} aria-label='Search people, organization records, locations, positions, and audit activity' onChange={event => { setQuery(event.target.value); setActiveIndex(0) }} onKeyDown={onSearchKeyDown} placeholder='Search people, teams, places, and activity…' ref={input} role='combobox' value={query} /></div>
				<div aria-label='Workspace search results' className='command-list' id='command-results' role='listbox'>
					{navigationMatches.length ? <div aria-label='Navigate' className='command-section' role='group'><p className='command-section__label'>Navigate</p>{navigationMatches.map(command => <CommandOption active={options[activeOptionIndex]?.id === command.id} command={command} key={command.id} onMouseEnter={() => setActiveIndex(options.findIndex(option => option.id === command.id))} onSelect={select} />)}</div> : null}
					{(Object.entries(groupedResults) as Array<[SearchKind, CommandItem[]]>).map(([kind, commands]) => <div aria-label={searchKindLabels[kind]} className='command-section' key={kind} role='group'><p className='command-section__label'>{searchKindLabels[kind]}</p>{commands.map(command => <CommandOption active={options[activeOptionIndex]?.id === command.id} command={command} key={command.id} onMouseEnter={() => setActiveIndex(options.findIndex(option => option.id === command.id))} onSelect={select} />)}</div>)}
					{showSearching ? <p className='command-status'>Searching workspace…</p> : null}
					{status === 401 ? <p className='command-status'>Sign in to search workspace records.</p> : null}
					{status === 403 ? <p className='command-status'>Your role cannot search workspace records.</p> : null}
					{search.error && status !== 401 && status !== 403 ? <p className='command-status' role='alert'>Search is temporarily unavailable. You can still use navigation.</p> : null}
					{showNoResults ? <p className='command-status'>No workspace records match “{normalizedQuery}”.</p> : null}
				</div>
			</Dialog.Content>
		</Dialog.Portal>
	</Dialog.Root>
}

function CommandOption({ active, command, onMouseEnter, onSelect }: { active: boolean; command: CommandItem; onMouseEnter: () => void; onSelect: (href: string) => void }) {
	const Icon = command.icon

	return <button aria-selected={active} className='command-option' data-selected={active} id={command.id} onClick={() => onSelect(command.href)} onMouseEnter={onMouseEnter} role='option' type='button'>
		<Icon aria-hidden='true' size={16} />
		<span><strong>{command.label}</strong><small>{command.description}</small>{command.matchedFields?.length ? <small className='command-option__matches'>Matched: {command.matchedFields.join(', ')}</small> : null}</span>
		<kbd>↵</kbd>
	</button>
}
