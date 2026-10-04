export const pendingProvisioningRefreshMs = 5_000

export function provisioningFailureMessage(employee: { status: string; provisioningFailureReason: string | null }): string | null {
	return employee.status === 'ProvisioningFailed' && employee.provisioningFailureReason
		? employee.provisioningFailureReason
		: null
}

export function needsProvisioningRefresh(employees: ReadonlyArray<{ status: string }> | undefined): boolean {
	return employees?.some(employee => employee.status === 'PendingProvisioning') ?? false
}
