import { ArrowRight, Building2, Route, ShieldCheck } from 'lucide-react'

const capabilities = [
	{ title: 'Directory', description: 'Departments and reporting structure', icon: Building2 },
	{ title: 'Identity', description: 'OIDC authentication and role-aware access', icon: ShieldCheck },
	{ title: 'Traceability', description: 'Auditable changes across the organization', icon: Route }
]

export default function OverviewPage() {
	return (
		<div className='page'>
			<header className='page-heading'>
				<div>
					<p className='eyebrow'>DS platform</p>
					<h1>People operations, connected by design.</h1>
				</div>
				<p className='page-heading__description'>A workspace for the organization data and service capabilities behind every employee lifecycle change.</p>
			</header>

			<section aria-label='Platform overview' className='overview-grid'>
				<div className='introduction-panel'>
					<div className='introduction-panel__copy'>
						<h2>One clear view of how people, roles, and teams fit together.</h2>
						<p>The interface is being connected to the platform services in stages. Start with the organization directory, then follow work into people, positions, locations, and auditable activity.</p>
					</div>
					<div aria-label='Organization data flows through platform services' className='platform-flow'>
						<span><Building2 aria-hidden='true' size={16} /></span><i /><span><ArrowRight aria-hidden='true' size={15} /></span><i /><span><ShieldCheck aria-hidden='true' size={16} /></span>
					</div>
				</div>
				<div className='capability-panel'>
					<p className='panel-label'>Platform capabilities</p>
					<ul className='capability-list'>
						{capabilities.map(({ description, icon: Icon, title }) => <li key={title}><Icon aria-hidden='true' size={17} /><span><strong>{title}</strong><span>{description}</span></span></li>)}
					</ul>
				</div>
			</section>

			<section aria-label='Workspace areas' className='feature-grid'>
				<article className='feature-card'><span className='feature-card__number'>01</span><h2>Organization directory</h2><p>Browse the current department hierarchy from the directory service.</p></article>
				<article className='feature-card'><span className='feature-card__number'>02</span><h2>Role-based workspace</h2><p>Session and permissions will shape each action as the secured client integration lands.</p></article>
				<article className='feature-card'><span className='feature-card__number'>03</span><h2>Change visibility</h2><p>Activity will surface the history behind organizational changes from the audit service.</p></article>
			</section>
		</div>
	)
}
