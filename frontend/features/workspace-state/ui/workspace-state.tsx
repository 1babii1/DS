import type { LucideIcon } from 'lucide-react'

export function WorkspaceState({
	description,
	icon: Icon,
	label,
	title
}: {
	description: string
	icon: LucideIcon
	label: string
	title: string
}) {
	return (
		<div className='page'>
			<header className='page-heading'>
				<div>
					<p className='eyebrow'>{label}</p>
					<h1>{title}</h1>
				</div>
			</header>
			<section className='empty-state' aria-labelledby='workspace-state-title'>
				<div className='empty-state__icon'><Icon aria-hidden='true' size={21} /></div>
				<h2 id='workspace-state-title'>This workspace is ready for its service connection.</h2>
				<p>{description}</p>
			</section>
		</div>
	)
}
