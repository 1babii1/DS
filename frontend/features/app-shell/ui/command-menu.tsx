'use client'

import * as Dialog from '@radix-ui/react-dialog'
import { Command, Search } from 'lucide-react'
import { useRouter } from 'next/navigation'
import { useCallback, useEffect, useRef, useState } from 'react'

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
	const input = useRef<HTMLInputElement>(null)
	const router = useRouter()
	const openMenu = useCallback(() => { setQuery(''); setOpen(true) }, [])
	const filtered = commands.filter(command => `${command.label} ${command.description}`.toLowerCase().includes(query.toLowerCase()))

	useEffect(() => {
		const onKeyDown = (event: KeyboardEvent) => {
			if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === 'k') { event.preventDefault(); if (open) setOpen(false); else openMenu() }
			if (event.key === 'Escape') setOpen(false)
		}
		window.addEventListener('keydown', onKeyDown)
		return () => window.removeEventListener('keydown', onKeyDown)
	}, [open, openMenu])
	useEffect(() => { if (open) window.setTimeout(() => input.current?.focus(), 0) }, [open])

	return <Dialog.Root onOpenChange={setOpen} open={open}><Dialog.Trigger asChild><button aria-label='Open command menu' className='command-trigger' onClick={openMenu} type='button'><Search aria-hidden='true' size={15} /><span>Search workspace</span><kbd><Command size={11} />K</kbd></button></Dialog.Trigger><Dialog.Portal><Dialog.Overlay className='command-overlay' /><Dialog.Content aria-describedby='command-description' className='command-menu'><Dialog.Title className='sr-only'>Command navigation</Dialog.Title><Dialog.Description className='sr-only' id='command-description'>Search and open a workspace section.</Dialog.Description><div className='command-search'><Search aria-hidden='true' size={16} /><input aria-label='Search workspace commands' onChange={event => setQuery(event.target.value)} placeholder='Search workspace…' ref={input} value={query} /></div><div className='command-list'>{filtered.map(command => <button key={command.href} onClick={() => { router.push(command.href); setOpen(false) }} type='button'><span><strong>{command.label}</strong><small>{command.description}</small></span><kbd>↵</kbd></button>)}{filtered.length === 0 ? <p>No matching workspace command.</p> : null}</div></Dialog.Content></Dialog.Portal></Dialog.Root>
}
