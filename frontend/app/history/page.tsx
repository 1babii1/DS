import { OrgHistoryView } from '@/features/org-history/ui/org-history-view'

export const metadata = { title: 'Org history' }

export default function HistoryPage() {
	return <div className='page'>
		<header className='page-heading'>
			<div><p className='eyebrow'>Audit service</p><h1>Org history</h1></div>
			<p className='page-heading__description'>How the organization looked on any past day, rebuilt from the recorded changes: departments as they were named and placed, and who worked where.</p>
		</header>
		<OrgHistoryView />
	</div>
}
