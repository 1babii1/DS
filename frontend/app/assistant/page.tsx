import { AssistantPanel } from '@/features/assistant/ui/assistant-panel'

export const metadata = { title: 'Assistant' }

export default function AssistantPage() {
	return <section className='mx-auto flex w-full max-w-3xl flex-col gap-4 p-4'>
		<header>
			<h1 className='text-2xl font-semibold'>Assistant</h1>
			<p className='text-sm text-muted-foreground'>
				Runs on a local model. It can look things up as you and propose changes; nothing changes until you approve a card.
			</p>
		</header>
		<AssistantPanel />
	</section>
}
