import { LogIn, ShieldAlert } from 'lucide-react'
import Link from 'next/link'

type AccessStateProps = {
	resource: string
	status: 401 | 403
}

export function AccessState({ resource, status }: AccessStateProps) {
	if (status === 401) {
		return <section className='empty-state'>
			<div className='empty-state__icon'><LogIn aria-hidden='true' size={21} /></div>
			<h2>Sign in to view {resource}</h2>
			<p>This workspace area is available through an authenticated session.</p>
			<Link className='retry-button' href='/login'><LogIn aria-hidden='true' size={16} />Continue to sign in</Link>
		</section>
	}

	return <section className='empty-state' role='alert'>
		<div className='empty-state__icon'><ShieldAlert aria-hidden='true' size={21} /></div>
		<h2>You don&apos;t have access to {resource}</h2>
		<p>Your account is signed in, but its current role cannot open this workspace area.</p>
	</section>
}
