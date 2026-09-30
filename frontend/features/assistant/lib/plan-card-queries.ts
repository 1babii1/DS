type PlanAction = {
	kind: 'HireEmployee' | 'TransferEmployee' | 'GrantCurrency'
}

export const assistantPlanKeys = {
	preview: (token: string) => ['assistant-plan-preview', token] as const
}

export function affectedPlanQueryKeys(steps: readonly PlanAction[]) {
	const keys: string[][] = []
	if (
		steps.some(
			step =>
				step.kind === 'HireEmployee' || step.kind === 'TransferEmployee'
		)
	)
		keys.push(['employees'])
	if (steps.some(step => step.kind === 'GrantCurrency'))
		keys.push(['rewards'])

	keys.push(['audit'], ['workspace-search'])
	return keys
}
