'use client'

import { Coins, LoaderCircle } from 'lucide-react'

import { useEmployeeCard } from '@/entities/employees/api/use-employee-card'
import { balanceView } from '../lib/employee-balance'

const format = (value: number) => new Intl.NumberFormat('en-US', { maximumFractionDigits: 2 }).format(value)

// The balance on a person's card (ADR 0034). It comes from a copy the employee service keeps, which can trail a grant by a couple of seconds, so it
// says when the number is as of, and says it is updating when it knows it is behind instead of presenting an old number as current.
export function EmployeeBalance({ employeeId }: { employeeId: string }) {
	const card = useEmployeeCard(employeeId)
	if (card.isPending || card.error || !card.data) return null

	const view = balanceView(card.data, format)
	return (
		<p aria-live='polite' className='employee-balance'>
			<Coins aria-hidden='true' size={15} />
			<strong>{view.text}</strong>
			{view.asOf ? <span>as of {new Date(view.asOf).toLocaleTimeString()}</span> : null}
			{view.updating ? <span className='employee-balance__updating'><LoaderCircle aria-hidden='true' className='animate-spin' size={13} />updating</span> : null}
		</p>
	)
}
