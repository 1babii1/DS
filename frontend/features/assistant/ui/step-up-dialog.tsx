'use client'

import { Button } from '@/shared/ui/button'
import { Input } from '@/shared/ui/input'
import { zodResolver } from '@hookform/resolvers/zod'
import * as Dialog from '@radix-ui/react-dialog'
import { useMutation, useQuery } from '@tanstack/react-query'
import {
	LoaderCircle,
	MailCheck,
	ShieldCheck,
	Smartphone,
	X
} from 'lucide-react'
import { useState } from 'react'
import { useForm } from 'react-hook-form'
import { z } from 'zod'

const codeSchema = z.object({
	code: z
		.string()
		.trim()
		.min(6, 'Enter the 6-digit confirmation code.')
		.max(128)
})
type CodeValues = z.infer<typeof codeSchema>
type StepUpStatus = { twoFactorEnabled: boolean; elevatedUntil: string | null }

async function request(path: string, init?: RequestInit): Promise<Response> {
	const response = await fetch(`/api/step-up/${path}`, {
		...init,
		headers: { 'content-type': 'application/json', ...init?.headers }
	})
	if (response.ok) return response

	const body = (await response.json().catch(() => null)) as {
		detail?: string
	} | null
	throw new Error(body?.detail ?? 'Re-verification could not be completed.')
}

export function StepUpDialog({
	onOpenChange,
	onVerified,
	open
}: {
	onOpenChange(open: boolean): void
	onVerified(): void
	open: boolean
}) {
	const [emailRequested, setEmailRequested] = useState(false)
	const status = useQuery({
		queryKey: ['step-up-status'],
		queryFn: async () =>
			(await request('status')).json() as Promise<StepUpStatus>,
		enabled: open,
		staleTime: 15_000
	})
	const form = useForm<CodeValues>({
		resolver: zodResolver(codeSchema),
		defaultValues: { code: '' }
	})
	const sendEmail = useMutation({
		mutationFn: () => request('request-email-code', { method: 'POST' }),
		onSuccess: () => setEmailRequested(true)
	})
	const verify = useMutation({
		mutationFn: ({ code }: CodeValues) =>
			request('verify', {
				method: 'POST',
				body: JSON.stringify({ code })
			}),
		onSuccess: () => {
			form.reset()
			setEmailRequested(false)
			onVerified()
			onOpenChange(false)
		}
	})

	function handleOpenChange(nextOpen: boolean) {
		if (!nextOpen) {
			form.reset()
			setEmailRequested(false)
		}
		onOpenChange(nextOpen)
	}

	const needsCode = status.data?.twoFactorEnabled || emailRequested
	const error = status.error ?? sendEmail.error ?? verify.error

	return (
		<Dialog.Root onOpenChange={handleOpenChange} open={open}>
			<Dialog.Portal>
				<Dialog.Overlay className='step-up-dialog__overlay' />
				<Dialog.Content className='step-up-dialog'>
					<Dialog.Close
						aria-label='Close re-verification'
						className='step-up-dialog__close'
					>
						<X aria-hidden='true' size={16} />
					</Dialog.Close>
					<div className='step-up-dialog__icon'>
						<ShieldCheck aria-hidden='true' size={19} />
					</div>
					<Dialog.Title>Confirm it’s you</Dialog.Title>
					<Dialog.Description>
						Currency grants need a recent re-verification. It
						expires automatically after ten minutes.
					</Dialog.Description>
					{status.isPending ? (
						<p className='step-up-dialog__loading'>
							<LoaderCircle aria-hidden='true' size={15} />{' '}
							Checking your verification method…
						</p>
					) : needsCode ? (
						<form
							className='step-up-dialog__form'
							onSubmit={form.handleSubmit(values =>
								verify.mutate(values)
							)}
						>
							<label htmlFor='step-up-code'>
								{status.data?.twoFactorEnabled
									? 'Authenticator code'
									: 'Email confirmation code'}
							</label>
							<Input
								autoComplete='one-time-code'
								autoFocus
								inputMode='numeric'
								id='step-up-code'
								maxLength={128}
								placeholder='000000'
								spellCheck={false}
								{...form.register('code')}
							/>
							{form.formState.errors.code ? (
								<p
									className='step-up-dialog__error'
									role='alert'
								>
									{form.formState.errors.code.message}
								</p>
							) : null}
							<Button disabled={verify.isPending} type='submit'>
								{verify.isPending ? (
									<LoaderCircle
										aria-hidden='true'
										className='animate-spin'
									/>
								) : (
									<Smartphone aria-hidden='true' />
								)}
								Verify session
							</Button>
						</form>
					) : (
						<div className='step-up-dialog__email'>
							<MailCheck aria-hidden='true' size={18} />
							<p>
								We’ll send a short-lived code to the email
								address on your account.
							</p>
							<Button
								disabled={sendEmail.isPending}
								onClick={() => sendEmail.mutate()}
								type='button'
							>
								{sendEmail.isPending ? (
									<LoaderCircle
										aria-hidden='true'
										className='animate-spin'
									/>
								) : null}
								Send confirmation code
							</Button>
						</div>
					)}
					{error instanceof Error ? (
						<p className='step-up-dialog__error' role='alert'>
							{error.message}
						</p>
					) : null}
					<p className='step-up-dialog__footnote'>
						After verification, return to this proposal and approve
						it explicitly.
					</p>
				</Dialog.Content>
			</Dialog.Portal>
		</Dialog.Root>
	)
}
