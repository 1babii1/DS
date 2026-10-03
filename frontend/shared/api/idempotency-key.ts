export type IdempotencyKey = { key: string; snapshot: string }

function stableSnapshot(value: Record<string, unknown>): string {
	return JSON.stringify(Object.fromEntries(Object.entries(value).sort(([left], [right]) => left.localeCompare(right))))
}

export function idempotencyKeyFor(current: IdempotencyKey | null, value: Record<string, unknown>, create: () => string): IdempotencyKey {
	const snapshot = stableSnapshot(value)
	return current?.snapshot === snapshot ? current : { key: create(), snapshot }
}
