import { ArrowDownRight, Database, KeyRound, Network, ShieldCheck, Webhook } from 'lucide-react'

const services = [
	{ name: 'AuthService', detail: 'OpenIddict issuer and identity boundary', icon: KeyRound },
	{ name: 'DirectoryService', detail: 'Departments, positions, and locations', icon: Network },
	{ name: 'EmployeeService', detail: 'Employee lifecycle and validation', icon: ShieldCheck },
	{ name: 'AuditService', detail: 'Durable change history from events', icon: Webhook }
]

export default function EngineeringPage() {
	return <div className='page engineering-page'>
		<header className='page-heading'><div><p className='eyebrow'>Platform delivery</p><h1>Engineering view</h1></div><p className='page-heading__description'>The frontend is a protected edge for a distributed people operations platform.</p></header>
		<section className='architecture-panel' aria-labelledby='boundary-heading'><div><p className='panel-label'>Request boundary</p><h2 id='boundary-heading'>The browser holds a session. The server holds the tokens.</h2><p>Authentication happens through OpenID Connect with Authorization Code and PKCE. Auth.js persists its opaque session in PostgreSQL; the Next.js BFF reads or refreshes access tokens only on the server.</p></div><div className='architecture-flow' aria-label='Browser to service data flow'><span>Browser</span><ArrowDownRight aria-hidden='true'/><span>Next.js<br/>BFF</span><ArrowDownRight aria-hidden='true'/><span>Services</span></div></section>
		<section className='service-grid' aria-label='Platform services'>{services.map(({ detail, icon: Icon, name }) => <article className='service-card' key={name}><Icon aria-hidden='true' size={19}/><h2>{name}</h2><p>{detail}</p></article>)}</section>
		<section className='engineering-notes'><article><Database aria-hidden='true' size={18}/><div><h2>Data boundaries</h2><p>Each service owns its schema. The frontend stores only UI state; Auth.js account and session records live in the dedicated <code>web_auth</code> schema.</p></div></article><article><Webhook aria-hidden='true' size={18}/><div><h2>Traceable changes</h2><p>Employee lifecycle events pass through the outbox and Kafka into AuditService, then appear in the Activity timeline.</p></div></article></section>
	</div>
}
