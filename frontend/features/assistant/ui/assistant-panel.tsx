'use client'

import { PlanCard } from './plan-card'
import { Button } from '@/shared/ui/button'
import axios, { isAxiosError } from 'axios'
import {
	Bot,
	CornerDownLeft,
	MessageCircleQuestion,
	SendHorizontal,
	Sparkles
} from 'lucide-react'
import { useEffect, useRef, useState } from 'react'
import type { FormEvent, KeyboardEvent } from 'react'

type Turn = { role: 'user' | 'assistant'; content: string; plans?: string[] }

const MaxKept = 30
const suggestedPrompts = [
	'How many people work in Payments?',
	'Show me the structure of the Engineering organization.',
	'What changed in the organization this week?'
]

// Conversation is deliberately local to this page. The model reply is text only;
// proposals are represented exclusively by their server-issued plan cards.
export function AssistantPanel() {
	const [turns, setTurns] = useState<Turn[]>([])
	const [text, setText] = useState('')
	const [busy, setBusy] = useState(false)
	const [problem, setProblem] = useState<string | null>(null)
	const end = useRef<HTMLDivElement>(null)
	const composer = useRef<HTMLTextAreaElement>(null)

	useEffect(() => {
		end.current?.scrollIntoView({ block: 'end', behavior: 'smooth' })
	}, [busy, turns.length])

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
			const { data } = await axios.post<{
				reply: string
				plans: string[]
			}>('/api/assistant', {
				messages: next
					.slice(-MaxKept)
					.map(({ role, content: body }) => ({ role, content: body }))
			})
			setTurns([
				...next,
				{ role: 'assistant', content: data.reply, plans: data.plans }
			])
		} catch (error) {
			const detail = isAxiosError(error)
				? (error.response?.data as { detail?: string } | undefined)
						?.detail
				: undefined
			setProblem(detail ?? 'The assistant could not be reached.')
		} finally {
			setBusy(false)
		}
	}

	function onComposerKeyDown(event: KeyboardEvent<HTMLTextAreaElement>) {
		if (event.key !== 'Enter' || event.shiftKey || busy) return
		event.preventDefault()
		event.currentTarget.form?.requestSubmit()
	}

	function choosePrompt(prompt: string) {
		setText(prompt)
		composer.current?.focus()
	}

	return (
		<div className='assistant-workspace'>
			<section
				aria-label='Assistant conversation'
				className='assistant-conversation'
			>
				<div className='assistant-conversation__bar'>
					<div>
						<span
							aria-hidden='true'
							className='assistant-conversation__status'
						/>
						<span>Conversation</span>
					</div>
					<p>Drafts expire automatically</p>
				</div>
				<div
					aria-live='polite'
					className='assistant-transcript'
					role='log'
				>
					{turns.length === 0 ? (
						<div className='assistant-empty'>
							<span
								aria-hidden='true'
								className='assistant-empty__icon'
							>
								<MessageCircleQuestion size={19} />
							</span>
							<div>
								<p className='assistant-empty__eyebrow'>
									Start with a question
								</p>
								<h2>Give the assistant a focused task.</h2>
								<p>
									It can read workspace data in your session.
									When a request would change something, it
									returns a separate proposal card for you to
									inspect.
								</p>
							</div>
							<div
								aria-label='Suggested questions'
								className='assistant-suggestions'
							>
								{suggestedPrompts.map(prompt => (
									<button
										key={prompt}
										onClick={() => choosePrompt(prompt)}
										type='button'
									>
										<Sparkles
											aria-hidden='true'
											size={14}
										/>
										{prompt}
									</button>
								))}
							</div>
						</div>
					) : null}
					{turns.map((turn, index) => (
						<article
							className={`assistant-turn assistant-turn--${turn.role}`}
							key={index}
						>
							<div className='assistant-turn__label'>
								{turn.role === 'user' ? (
									'You'
								) : (
									<>
										<Bot aria-hidden='true' size={14} />{' '}
										Assistant
									</>
								)}
							</div>
							<p>{turn.content}</p>
							{turn.plans?.map(token => (
								<PlanCard key={token} token={token} />
							))}
						</article>
					))}
					{busy ? (
						<div className='assistant-thinking'>
							<span aria-hidden='true' />
							<span>Reviewing your workspace…</span>
						</div>
					) : null}
					<div ref={end} />
				</div>
				{problem ? (
					<p className='assistant-problem' role='alert'>
						{problem}
					</p>
				) : null}
				<form className='assistant-composer' onSubmit={send}>
					<label htmlFor='assistant-message'>Message</label>
					<div>
						<textarea
							aria-describedby='assistant-composer-hint'
							disabled={busy}
							id='assistant-message'
							maxLength={4000}
							onChange={event => setText(event.target.value)}
							onKeyDown={onComposerKeyDown}
							placeholder='Ask about your organization or describe a change…'
							ref={composer}
							rows={2}
							value={text}
						/>
						<Button
							aria-label='Send message'
							disabled={busy || !text.trim()}
							size='icon'
							type='submit'
						>
							<SendHorizontal aria-hidden='true' size={16} />
						</Button>
					</div>
					<p id='assistant-composer-hint'>
						<CornerDownLeft aria-hidden='true' size={13} /> Enter to
						send · Shift + Enter for a new line{' '}
						<span>{text.length}/4000</span>
					</p>
				</form>
			</section>
			<aside aria-label='How proposals work' className='assistant-guide'>
				<p className='panel-label'>How it works</p>
				<ol>
					<li>
						<span>01</span>
						<div>
							<strong>Ask in plain language</strong>
							<p>
								The assistant reads only the workspace
								information available to your session.
							</p>
						</div>
					</li>
					<li>
						<span>02</span>
						<div>
							<strong>Review a server-signed proposal</strong>
							<p>
								Names, departments, amounts, and reasons come
								from the server, not model text.
							</p>
						</div>
					</li>
					<li>
						<span>03</span>
						<div>
							<strong>Approve deliberately</strong>
							<p>
								Your click is the only action that can apply a
								proposal. Currency grants require recent
								verification.
							</p>
						</div>
					</li>
				</ol>
			</aside>
		</div>
	)
}
