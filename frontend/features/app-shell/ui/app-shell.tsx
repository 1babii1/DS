'use client'

import {
	Activity,
	BadgeCheck,
	BriefcaseBusiness,
	Building2,
	ChevronRight,
	LayoutDashboard,
	MapPin,
	Menu,
	UsersRound,
	Workflow
} from 'lucide-react'
import Link from 'next/link'
import { usePathname } from 'next/navigation'
import type { ComponentType, ReactNode } from 'react'

import { NotificationBell } from '@/features/notifications/ui/notification-bell'
import { WalletBalance } from '@/features/rewards/ui/wallet-balance'
import { Sheet, SheetContent, SheetTitle, SheetTrigger } from '@/shared/ui/sheet'

import { CommandMenu } from './command-menu'
import { ThemeToggle } from './theme-toggle'

const navigation: ReadonlyArray<{
	description: string
	href: string
	icon: ComponentType<{ size?: number; strokeWidth?: number }>
	label: string
}> = [
	{ label: 'Overview', href: '/', icon: LayoutDashboard, description: 'Workspace overview' },
	{ label: 'Organization', href: '/departments', icon: Building2, description: 'Department structure' },
	{ label: 'People', href: '/people', icon: UsersRound, description: 'Employee directory' },
	{ label: 'Positions', href: '/positions', icon: BriefcaseBusiness, description: 'Role catalogue' },
	{ label: 'Locations', href: '/locations', icon: MapPin, description: 'Office footprint' },
	{ label: 'Activity', href: '/activity', icon: Activity, description: 'Audit events' }
]

function isActive(pathname: string, href: string) {
	return href === '/' ? pathname === href : pathname.startsWith(href)
}

function ProductMark() {
	return (
		<Link aria-label='DS workspace overview' className='product-mark' href='/'>
			<span aria-hidden='true' className='product-mark__glyph'>
				<Workflow size={17} strokeWidth={2.4} />
			</span>
			<span>
				<strong>DS</strong>
				<span>People &amp; Organization</span>
			</span>
		</Link>
	)
}

function Navigation({ compact = false }: { compact?: boolean }) {
	const pathname = usePathname()

	return (
		<nav aria-label='Workspace navigation' className={compact ? 'nav-list nav-list--compact' : 'nav-list'}>
			<p className='nav-list__label'>Workspace</p>
			{navigation.map(({ description, href, icon: Icon, label }) => {
				const active = isActive(pathname, href)
				return (
					<Link aria-current={active ? 'page' : undefined} className='nav-list__item' data-active={active} href={href} key={href}>
						<Icon aria-hidden='true' size={18} strokeWidth={active ? 2.4 : 1.9} />
						<span>{label}</span>
						{compact ? <small>{description}</small> : null}
					</Link>
				)
			})}
		</nav>
	)
}

function Sidebar() {
	return (
		<aside className='app-sidebar'>
			<ProductMark />
			<Navigation />
			<div className='app-sidebar__footer'>
				<Link className='engineering-link' href='/engineering'>
					<BadgeCheck aria-hidden='true' size={17} />
					<span>
						<strong>Engineering view</strong>
						<small>Architecture &amp; delivery</small>
					</span>
					<ChevronRight aria-hidden='true' size={16} />
				</Link>
			</div>
		</aside>
	)
}

function MobileNavigation() {
	return (
		<Sheet>
			<SheetTrigger asChild>
				<button aria-label='Open navigation' className='icon-button mobile-menu' type='button'>
					<Menu aria-hidden='true' size={20} />
				</button>
			</SheetTrigger>
			<SheetContent className='mobile-sheet' side='left'>
				<SheetTitle className='sr-only'>Workspace navigation</SheetTitle>
				<ProductMark />
				<Navigation compact />
				<Link className='engineering-link mobile-engineering-link' href='/engineering'>
					<BadgeCheck aria-hidden='true' size={17} />
					<span>
						<strong>Engineering view</strong>
						<small>Architecture &amp; delivery</small>
					</span>
				</Link>
			</SheetContent>
		</Sheet>
	)
}

export function AppShell({ accountControl, authenticated, children }: { accountControl: ReactNode; authenticated: boolean; children: ReactNode }) {
	const pathname = usePathname()
	if (pathname.startsWith('/login') || pathname.startsWith('/register')) return <>{children}</>

	return (
		<div className='app-frame'>
			<a className='skip-link' href='#main-content'>Skip to content</a>
			<Sidebar />
			<div className='app-content'>
				<header className='app-topbar'>
					<div className='app-topbar__mobile'><MobileNavigation /></div>
					<p className='app-topbar__context'>People operations workspace</p>
					<div className='app-topbar__actions'>
						<span className='environment-badge'><span aria-hidden='true' />Local environment</span>
				<CommandMenu />
				<WalletBalance enabled={authenticated} />
						<NotificationBell enabled={authenticated} />
						{accountControl}
						<ThemeToggle />
					</div>
				</header>
				<main id='main-content'>{children}</main>
			</div>
		</div>
	)
}
