'use client'

import * as Dialog from '@radix-ui/react-dialog'
import { Command, Search } from 'lucide-react'
import { useRouter } from 'next/navigation'
import { useCallback, useEffect, useRef, useState } from 'react'
import type { KeyboardEvent as ReactKeyboardEvent } from 'react'

const commands = [
	{ href: '/', label: 'Overview', description: 'Workspace overview' },
	{ href: '/departments', label: 'Organization', description: 'Department structure' },
	{ href: '/people', label: 'People', description: 'Employee directory' },
	{ href: '/positions', label: 'Positions', description: 'Role catalogue' },
	{ href: '/locations', label: 'Locations', description: 'Office footprint' },
	{ href: '/activity', label: 'Activity', description: 'Audit events' },
	{ href: '/engineering', label: 'Engineering view', description: 'Architecture and delivery' },
]

export function CommandMenu() {
	const [open, setOpen] = useState(false)
	const [query, setQuery] = useState('')
	const [activeIndex, setActiveIndex] = useState(0)
	const input = useRef<HTMLInputElement>(null)
	const router = useRouter()
	const openMenu = useCallback(() => { setQuery(''); setActiveIndex(0); setOpen(true) }, [])
	const filtered = commands.filter(command => `${command.label} ${command.description}`.toLowerCase().includes(query.toLowerCase()))
	const select = useCallback((href: string) => { router.push(href); setOpen(false) }, [router])

	useEffect(() => {
		const onKeyDown = (event: KeyboardEvent) => {
			if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === 'k') { event.preventDefault(); if (open) setOpen(false); else openMenu() }
			if (event.key === 'Escape') setOpen(false)
		}
		window.addEventListener('keydown', onKeyDown)
		return () => window.removeEventListener('keydown', onKeyDown)
	}, [open, openMenu])
	useEffect(() => { if (open) window.setTimeout(() => input.current?.focus(), 0) }, [open])

	const onSearchKeyDown = (event: ReactKeyboardEvent<HTMLInputElement>) => {
		if (event.key === 'ArrowDown' && filtered.length) { event.preventDefault(); setActiveIndex(index => Math.min(index + 1, filtered.length - 1)) }
		if (event.key === 'ArrowUp' && filtered.length) { event.preventDefault(); setActiveIndex(index => Math.max(index - 1, 0)) }
		if (event.key === 'Enter' && filtered[activeIndex]) { event.preventDefault(); select(filtered[activeIndex].href) }
	}

	return <Dialog.Root onOpenChange={setOpen} open={open}><Dialog.Trigger asChild><button aria-label='Open command menu' className='command-trigger' onClick={openMenu} type='button'><Search aria-hidden='true' size={15} /><span>Search workspace</span><kbd><Command size={11} />K</kbd></button></Dialog.Trigger><Dialog.Portal><Dialog.Overlay className='command-overlay' /><Dialog.Content aria-describedby='command-description' className='command-menu'><Dialog.Title className='sr-only'>Command navigation</Dialog.Title><Dialog.Description className='sr-only' id='command-description'>Search and open a workspace section.</Dialog.Description><div className='command-search'><Search aria-hidden='true' size={16} /><input aria-activedescendant={filtered[activeIndex] ? `command-${filtered[activeIndex].href.slice(1) || 'overview'}` : undefined} aria-autocomplete='list' aria-controls='command-results' aria-expanded={open} aria-label='Search workspace commands' onChange={event => { setQuery(event.target.value); setActiveIndex(0) }} onKeyDown={onSearchKeyDown} placeholder='Search workspace…' ref={input} role='combobox' value={query} /></div><div aria-label='Workspace commands' className='command-list' id='command-results' role='listbox'>{filtered.map((command, index) => <button aria-selected={index === activeIndex} data-selected={index === activeIndex} id={`command-${command.href.slice(1) || 'overview'}`} key={command.href} onClick={() => select(command.href)} onMouseEnter={() => setActiveIndex(index)} role='option' type='button'><span><strong>{command.label}</strong><small>{command.description}</small></span><kbd>↵</kbd></button>)}{filtered.length === 0 ? <p>No matching workspace command.</p> : null}</div></Dialog.Content></Dialog.Portal></Dialog.Root>
}
