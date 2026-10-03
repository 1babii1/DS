// The wallet version the backend last gave this browser for each employee, in X-Wallet-Version on a grant (ADR 0034). The employee card is
// a copy that trails the wallet by up to a couple of seconds; sending this version back with the next read of the card makes the backend
// wait for the copy to reach it instead of showing the balance from before the grant. Held in memory: it only matters for a few seconds.
const versions = new Map<string, number>()

export function rememberWalletVersion(employeeId: string, header: unknown): void {
	const text = String(header ?? '')
	if (!/^\d+$/.test(text)) return
	const version = Number.parseInt(text, 10)
	if (version < 1) return
	if (version > (versions.get(employeeId) ?? 0)) versions.set(employeeId, version)
}

export function walletVersionFor(employeeId: string): number | undefined {
	return versions.get(employeeId)
}

export function forgetWalletVersions(): void {
	versions.clear()
}
