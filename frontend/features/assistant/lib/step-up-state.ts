export type PlanAction = 'approve' | 'reverify'

export function nextPlanAction(
	needsReverification: boolean,
	elevatedUntil: string | null,
	now = new Date()
): PlanAction {
	if (!needsReverification) return 'approve'
	if (!elevatedUntil) return 'reverify'

	return new Date(elevatedUntil) > now ? 'approve' : 'reverify'
}
