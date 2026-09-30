import { LogIn, LogOut } from 'lucide-react'
import Link from 'next/link'

import { signOut } from '@/auth'
import { revokeCurrentProviderAccount } from '@/shared/auth/access-token'

type SessionUser = { email?: string | null; id: string; name?: string | null } | undefined

function initials(user: NonNullable<SessionUser>): string {
	const source = user.name?.trim() || user.email?.trim() || 'DS'
	return source.split(/\s|@/).filter(Boolean).slice(0, 2).map(part => part[0]).join('').toUpperCase()
}

export function SessionControl({ user }: { user: SessionUser }) {
	if (!user) {
		return <Link className='sign-in-link' href='/login'><LogIn aria-hidden='true' size={16} />Sign in</Link>
	}

	return <form action={async () => { 'use server'; await revokeCurrentProviderAccount(user.id); await signOut({ redirectTo: '/login' }) }}>
		<button aria-label='Sign out' className='account-control' title='Sign out' type='submit'>
			<span aria-hidden='true' className='account-control__initials'>{initials(user)}</span>
			<span className='account-control__label'>{user.name || user.email || 'Account'}</span>
			<LogOut aria-hidden='true' size={15} />
		</button>
	</form>
}
