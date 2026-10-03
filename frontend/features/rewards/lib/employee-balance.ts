import type { EmployeeCard } from '@/entities/employees/types/employee.types'

export type BalanceView = { text: string; asOf: string | null; updating: boolean }

// What the card says about the balance, in words. A null balance means no wallet event has arrived yet, which is not a balance of zero;
// a card that is not consistent is showing a number from before the last grant, so it is shown as being updated, not as current.
export function balanceView(card: Pick<EmployeeCard, 'balance' | 'balanceChangedAt' | 'consistent'>, format: (value: number) => string): BalanceView {
	if (card.balance === null) return { text: 'No balance yet', asOf: null, updating: !card.consistent }
	return { text: `${format(card.balance)} credits`, asOf: card.balanceChangedAt, updating: !card.consistent }
}
