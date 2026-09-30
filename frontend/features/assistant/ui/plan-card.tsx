'use client'

import {
	affectedPlanQueryKeys,
	assistantPlanKeys
} from '@/features/assistant/lib/plan-card-queries'
import { describeProposalExpiry } from '@/features/assistant/lib/plan-card-state'
import { axiosInstance } from '@/shared/api/axiosInstance'
import { Button } from '@/shared/ui/button'
import {
	Card,
	CardContent,
	CardFooter,
	CardHeader,
	CardTitle
} from '@/shared/ui/card'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { isAxiosError } from 'axios'
import {
	BadgeCheck,
	CircleAlert,
	CircleCheck,
	Clock3,
	Landmark,
	ShieldCheck,
	UserRoundCheck,
	UsersRound
} from 'lucide-react'

type PlanCardStep = {
	kind: 'HireEmployee' | 'TransferEmployee' | 'GrantCurrency'
	summary: string
	amount: number | null
	departmentName: string | null
	email: string | null
	employeeName: string | null
	fullName: string | null
	positionName: string | null
	reason: string | null
}

type PlanCardData = {
	expiresAt: string
	needsReverification: boolean
	steps: PlanCardStep[]
}
type StepResult = {
	index: number
	summary: string
	outcome: 'Applied' | 'Failed' | 'NotRun'
	detail: string | null
}
type Report = { completed: boolean; steps: StepResult[] }

const kindLabel: Record<PlanCardStep['kind'], string> = {
	HireEmployee: 'Hire',
	TransferEmployee: 'Transfer',
	GrantCurrency: 'Grant'
}

const kindIcon: Record<PlanCardStep['kind'], typeof UsersRound> = {
	HireEmployee: UserRoundCheck,
	TransferEmployee: UsersRound,
	GrantCurrency: Landmark
}

function answerOf(error: unknown, fallback: string): string {
	if (isAxiosError(error)) {
		const detail =
			(error.response?.data as
				{ error?: string; detail?: string } | undefined) ?? {}
		return detail.error ?? detail.detail ?? fallback
	}
	return fallback
}

// Every visible action detail comes from the signed-plan preview. The model reply stays untrusted display text.
export function PlanCard({ token }: { token: string }) {
	const queryClient = useQueryClient()
	const preview = useQuery({
		queryKey: assistantPlanKeys.preview(token),
		queryFn: async () =>
			(
				await axiosInstance.post<PlanCardData>('/mcp/plans/preview', {
					token
				})
			).data,
		retry: false,
		staleTime: Infinity
	})
	const confirm = useMutation({
		mutationFn: async () =>
			(await axiosInstance.post<Report>('/mcp/plans/confirm', { token }))
				.data,
		onSuccess: async report => {
			if (!report.steps.some(step => step.outcome === 'Applied')) return
			await Promise.all(
				affectedPlanQueryKeys(preview.data?.steps ?? []).map(queryKey =>
					queryClient.invalidateQueries({ queryKey })
				)
			)
		}
	})

	if (preview.isPending)
		return (
			<Card aria-busy='true' className='proposal-card'>
				<CardContent className='proposal-card__loading'>
					<span aria-hidden='true' />
					Reading the server-signed proposal…
				</CardContent>
			</Card>
		)
	if (preview.isError)
		return (
			<Card className='proposal-card'>
				<CardContent className='proposal-card__error'>
					<CircleAlert
						aria-hidden='true'
						className='mt-0.5 shrink-0'
						size={16}
					/>
					<span>
						{answerOf(
							preview.error,
							'This proposal can no longer be shown. Ask the assistant again.'
						)}
					</span>
				</CardContent>
			</Card>
		)

	const card = preview.data
	const report = confirm.data
	const expiry = describeProposalExpiry(card.expiresAt)

	return (
		<Card className='proposal-card'>
			<CardHeader className='proposal-card__header'>
				<div>
					<p className='proposal-card__eyebrow'>
						<ShieldCheck aria-hidden='true' size={14} /> Review
						before applying
					</p>
					<CardTitle>Proposed changes</CardTitle>
				</div>
				<span
					className={`proposal-card__expiry${expiry.expired ? ' proposal-card__expiry--expired' : ''}`}
				>
					<Clock3 aria-hidden='true' size={14} />
					{expiry.label}
				</span>
			</CardHeader>
			<CardContent className='proposal-card__content'>
				<p className='proposal-card__notice'>
					Nothing has changed. These details were read from the
					server-signed plan and will be checked again when you
					approve.
				</p>
				<ol className='proposal-card__steps'>
					{card.steps.map((step, index) => {
						const Icon = kindIcon[step.kind]
						const details = [
							step.fullName ? ['Person', step.fullName] : null,
							step.employeeName
								? ['Employee', step.employeeName]
								: null,
							step.email ? ['Email', step.email] : null,
							step.departmentName
								? ['Department', step.departmentName]
								: null,
							step.positionName
								? ['Position', step.positionName]
								: null,
							step.amount !== null
								? ['Amount', String(step.amount)]
								: null,
							step.reason ? ['Reason', step.reason] : null
						].filter(
							(detail): detail is [string, string] =>
								detail !== null
						)
						return (
							<li key={index}>
								<div className='proposal-card__step-icon'>
									<Icon aria-hidden='true' size={16} />
								</div>
								<div>
									<p>
										<span>
											{String(index + 1).padStart(2, '0')}
										</span>
										{kindLabel[step.kind]}
									</p>
									<strong>{step.summary}</strong>
									{details.length ? (
										<dl>
											{details.map(([label, value]) => (
												<div key={label}>
													<dt>{label}</dt>
													<dd>{value}</dd>
												</div>
											))}
										</dl>
									) : null}
								</div>
							</li>
						)
					})}
				</ol>
				{card.needsReverification && !report ? (
					<p className='proposal-card__reverify'>
						<BadgeCheck aria-hidden='true' size={16} />
						Handing out currency needs a recent re-verification of
						your sign-in. The server remains the final check.
					</p>
				) : null}
				{confirm.isError ? (
					<p className='proposal-card__confirm-error' role='alert'>
						<CircleAlert
							aria-hidden='true'
							className='mt-0.5 shrink-0'
							size={16}
						/>
						{answerOf(
							confirm.error,
							'The change could not be applied.'
						)}
					</p>
				) : null}
				{report ? (
					<ul aria-live='polite' className='proposal-card__report'>
						{report.steps.map(step => (
							<li
								className={`proposal-card__report-item proposal-card__report-item--${step.outcome.toLowerCase()}`}
								key={step.index}
							>
								{step.outcome === 'Applied' ? (
									<CircleCheck
										aria-hidden='true'
										className='mt-0.5 shrink-0'
										size={16}
									/>
								) : (
									<CircleAlert
										aria-hidden='true'
										className='mt-0.5 shrink-0'
										size={16}
									/>
								)}
								<span>
									{step.summary}: {step.outcome}
									{step.detail ? `: ${step.detail}` : ''}
								</span>
							</li>
						))}
					</ul>
				) : null}
			</CardContent>
			<CardFooter className='proposal-card__footer'>
				<div>
					<strong>
						{report
							? report.completed
								? 'Proposal completed'
								: 'Proposal needs attention'
							: expiry.expired
								? 'Proposal expired'
								: 'Your approval is required'}
					</strong>
					<span>
						{report
							? 'See the result for each requested action.'
							: expiry.expired
								? 'Ask the assistant to create a fresh proposal.'
								: 'Approval applies only the actions listed above.'}
					</span>
				</div>
				<Button
					disabled={
						confirm.isPending || Boolean(report) || expiry.expired
					}
					onClick={() => confirm.mutate()}
					type='button'
				>
					{confirm.isPending
						? 'Applying…'
						: report
							? 'Done'
							: expiry.expired
								? 'Expired'
								: 'Approve and apply'}
				</Button>
			</CardFooter>
		</Card>
	)
}
