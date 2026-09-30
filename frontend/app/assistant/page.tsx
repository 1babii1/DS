import { AssistantPanel } from '@/features/assistant/ui/assistant-panel'
import { Bot, ShieldCheck, Sparkles } from 'lucide-react'

export const metadata = { title: 'Assistant' }

export default function AssistantPage() {
	return (
		<section className='page assistant-page'>
			<header className='assistant-page__heading'>
				<div>
					<p className='eyebrow'>
						<Sparkles aria-hidden='true' size={13} /> Local
						operations assistant
					</p>
					<h1>Ask for context. Review every change.</h1>
					<p>
						The assistant can research your organization and draft
						hires, transfers, or grants. A draft stays inert until
						you inspect and approve it.
					</p>
				</div>
				<div
					aria-label='Assistant safety guarantees'
					className='assistant-page__principles'
				>
					<span>
						<Bot aria-hidden='true' size={16} /> Local model
					</span>
					<span>
						<ShieldCheck aria-hidden='true' size={16} /> Human
						approval required
					</span>
				</div>
			</header>
			<AssistantPanel />
		</section>
	)
}
