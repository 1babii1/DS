'use client'

import { useMutation, useQuery } from '@tanstack/react-query'
import { isAxiosError } from 'axios'
import { CircleAlert, CircleCheck, ShieldCheck } from 'lucide-react'

import { axiosInstance } from '@/shared/api/axiosInstance'
import { Button } from '@/shared/ui/button'
import { Card, CardContent, CardFooter, CardHeader, CardTitle } from '@/shared/ui/card'

type PlanCardStep = {
	kind: 'HireEmployee' | 'TransferEmployee' | 'GrantCurrency'
	summary: string
	amount: number | null
	reason: string | null
}

type PlanCardData = { expiresAt: string; needsReverification: boolean; steps: PlanCardStep[] }

type StepResult = { index: number; summary: string; outcome: 'Applied' | 'Failed' | 'NotRun'; detail: string | null }

type Report = { completed: boolean; steps: StepResult[] }

const kindLabel: Record<PlanCardStep['kind'], string> = {
	HireEmployee: 'Hire',
	TransferEmployee: 'Transfer',
	GrantCurrency: 'Grant'
}

function answerOf(error: unknown, fallback: string): string {
	if (isAxiosError(error)) {
		const detail = (error.response?.data as { error?: string; detail?: string } | undefined) ?? {}
		return detail.error ?? detail.detail ?? fallback
	}
	return fallback
}

// The approval card. Everything on it comes from the server's reading of the signed plan (preview), not from what
// the model said; the only way to run the plan is the button here, which is the person's own request. Text is
// rendered as text: a name that says "ignore the limit" is shown as a name.
export function PlanCard({ token }: { token: string }) {
	const preview = useQuery({
		queryKey: ['assistant-plan-preview', token],
		queryFn: async () => (await axiosInstance.post<PlanCardData>('/mcp/plans/preview', { token })).data,
		retry: false,
		staleTime: Infinity
	})
	const confirm = useMutation({
		mutationFn: async () => (await axiosInstance.post<Report>('/mcp/plans/confirm', { token })).data
	})

	if (preview.isPending) return <Card aria-busy='true'><CardContent className='p-4 text-sm'>Reading the proposal…</CardContent></Card>

	if (preview.isError) {
		return <Card><CardContent className='flex items-start gap-2 p-4 text-sm'>
			<CircleAlert aria-hidden='true' className='mt-0.5 shrink-0' size={16} />
			<span>{answerOf(preview.error, 'This proposal can no longer be shown. Ask the assistant again.')}</span>
		</CardContent></Card>
	}

	const card = preview.data
	const report = confirm.data

	return <Card>
		<CardHeader className='pb-2'>
			<CardTitle className='flex items-center gap-2 text-base'>
				<ShieldCheck aria-hidden='true' size={16} /> Proposed changes: nothing has happened yet
			</CardTitle>
		</CardHeader>
		<CardContent className='space-y-2 text-sm'>
			<ol className='list-decimal space-y-2 pl-5'>
				{card.steps.map((step, index) => <li key={index}>
					<strong>{kindLabel[step.kind]}</strong>: {step.summary}
				</li>)}
			</ol>
			{card.needsReverification && !report
				? <p className='text-muted-foreground'>Handing out currency needs a recent re-verification of your sign-in.</p>
				: null}
			{confirm.isError ? <p className='flex items-start gap-2' role='alert'>
				<CircleAlert aria-hidden='true' className='mt-0.5 shrink-0' size={16} />
				{answerOf(confirm.error, 'The change could not be applied.')}
			</p> : null}
			{report ? <ul aria-live='polite' className='space-y-1'>
				{report.steps.map(step => <li className='flex items-start gap-2' key={step.index}>
					{step.outcome === 'Applied'
						? <CircleCheck aria-hidden='true' className='mt-0.5 shrink-0' size={16} />
						: <CircleAlert aria-hidden='true' className='mt-0.5 shrink-0' size={16} />}
					<span>{step.outcome}{step.detail ? `: ${step.detail}` : ''}</span>
				</li>)}
			</ul> : null}
		</CardContent>
		<CardFooter>
			<Button disabled={confirm.isPending || Boolean(report)} onClick={() => confirm.mutate()} type='button'>
				{confirm.isPending ? 'Applying…' : report ? 'Done' : 'Approve and apply'}
			</Button>
		</CardFooter>
	</Card>
}
