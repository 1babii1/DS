'use client'

import { zodResolver } from '@hookform/resolvers/zod'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { LoaderCircle } from 'lucide-react'
import { useEffect } from 'react'
import { useForm } from 'react-hook-form'
import { z } from 'zod'

import { rewardsApi } from '@/entities/rewards/api/rewards.api'
import { mutationErrorMessage } from '@/shared/api/mutation-error'
import { useNotice } from '@/shared/ui/notice-provider'

const schema = z.object({ amount: z.number().positive('Enter a positive number of credits.').max(100_000, 'The maximum grant is 100,000 credits.'), reason: z.string().trim().min(3, 'Explain the reason in at least 3 characters.').max(500, 'Keep the reason under 500 characters.') })
type Values = z.infer<typeof schema>

export function RewardGrantForm({ employeeId, employeeName, onClose }: { employeeId: string; employeeName: string; onClose: () => void }) {
	const queryClient = useQueryClient()
	const { showSuccess } = useNotice()
	const form = useForm<Values>({ resolver: zodResolver(schema), defaultValues: { amount: 100, reason: '' } })
	useEffect(() => { form.setFocus('amount') }, [form])
	const grant = useMutation({ mutationFn: (values: Values) => rewardsApi.grant({ ...values, employeeId }), onSuccess: async () => { await queryClient.invalidateQueries({ queryKey: ['rewards'] }); showSuccess(`Reward granted to ${employeeName}.`); onClose() }, onError: error => form.setError('root', { message: mutationErrorMessage(error, 'Reward grant') }) })

	return <section aria-labelledby='reward-grant-title' className='employee-form-panel'><div className='employee-form-panel__heading'><div><p className='eyebrow'>Recognition</p><h2 id='reward-grant-title'>Reward {employeeName}</h2></div><button aria-label='Close reward form' className='icon-button' onClick={onClose} type='button'>×</button></div><form className='employee-form' onSubmit={form.handleSubmit(values => grant.mutate(values))}><label className='form-field'><span>Credits</span><input {...form.register('amount', { valueAsNumber: true })} aria-describedby={form.formState.errors.amount ? 'reward-amount-error' : undefined} aria-invalid={Boolean(form.formState.errors.amount)} inputMode='decimal' min='0.01' step='0.01' type='number' />{form.formState.errors.amount ? <small id='reward-amount-error' role='alert'>{form.formState.errors.amount.message}</small> : null}</label><label className='form-field'><span>Reason</span><input {...form.register('reason')} aria-describedby={form.formState.errors.reason ? 'reward-reason-error' : undefined} aria-invalid={Boolean(form.formState.errors.reason)} placeholder='Recognized for excellent project delivery' />{form.formState.errors.reason ? <small id='reward-reason-error' role='alert'>{form.formState.errors.reason.message}</small> : null}</label>{form.formState.errors.root ? <p className='form-error' role='alert'>{form.formState.errors.root.message}</p> : null}<button className='form-submit' disabled={grant.isPending} type='submit'>{grant.isPending ? <LoaderCircle aria-hidden='true' className='animate-spin' size={16} /> : null}Grant reward</button></form></section>
}
