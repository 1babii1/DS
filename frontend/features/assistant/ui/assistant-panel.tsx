'use client'

import axios, { isAxiosError } from 'axios'
import { SendHorizontal } from 'lucide-react'
import { useRef, useState } from 'react'
import type { FormEvent } from 'react'

import { Button } from '@/shared/ui/button'
import { Input } from '@/shared/ui/input'

import { PlanCard } from './plan-card'

type Turn = { role: 'user' | 'assistant'; content: string; plans?: string[] }

const MaxKept = 30

// The chat lives in the page only: reloading starts over. What the model proposes shows up as a card under its
// reply; the reply itself is just text and cannot approve anything.
export function AssistantPanel() {
	const [turns, setTurns] = useState<Turn[]>([])
	const [text, setText] = useState('')
	const [busy, setBusy] = useState(false)
	const [problem, setProblem] = useState<string | null>(null)
	const end = useRef<HTMLDivElement>(null)

	async function send(event: FormEvent) {
		event.preventDefault()
		const content = text.trim()
		if (!content || busy) return

		const next: Turn[] = [...turns, { role: 'user', content }]
		setTurns(next)
		setText('')
		setProblem(null)
		setBusy(true)
		try {
			const { data } = await axios.post<{ reply: string; plans: string[] }>('/api/assistant', {
				messages: next.slice(-MaxKept).map(({ role, content: body }) => ({ role, content: body }))
			})
			setTurns([...next, { role: 'assistant', content: data.reply, plans: data.plans }])
		} catch (error) {
			const detail = isAxiosError(error) ? (error.response?.data as { detail?: string } | undefined)?.detail : undefined
			setProblem(detail ?? 'The assistant could not be reached.')
		} finally {
			setBusy(false)
			queueMicrotask(() => end.current?.scrollIntoView({ block: 'end' }))
		}
	}

	return <div className='flex flex-col gap-4'>
		<div aria-live='polite' className='flex flex-col gap-3' role='log'>
			{turns.length === 0 ? <p className='text-sm text-muted-foreground'>
				Ask about the organization, or ask for a change (hire, transfer, grant). A change is only ever proposed here: you read it
				on a card and approve it yourself.
			</p> : null}
			{turns.map((turn, index) => <div className={turn.role === 'user' ? 'self-end max-w-[85%]' : 'self-start max-w-[85%]'} key={index}>
				<p className={turn.role === 'user' ? 'rounded-lg bg-primary px-3 py-2 text-sm text-primary-foreground' : 'rounded-lg bg-muted px-3 py-2 text-sm'}>
					{turn.content}
				</p>
				{turn.plans?.map(token => <div className='mt-2' key={token}><PlanCard token={token} /></div>)}
			</div>)}
			{busy ? <p className='self-start text-sm text-muted-foreground'>Working on it…</p> : null}
			<div ref={end} />
		</div>
		{problem ? <p className='text-sm' role='alert'>{problem}</p> : null}
		<form className='flex gap-2' onSubmit={send}>
			<Input aria-label='Message to the assistant' disabled={busy} maxLength={4000} onChange={event => setText(event.target.value)}
				placeholder='For example: how many people work in Payments?' value={text} />
			<Button aria-label='Send' disabled={busy || !text.trim()} type='submit'><SendHorizontal aria-hidden='true' size={16} /></Button>
		</form>
	</div>
}
