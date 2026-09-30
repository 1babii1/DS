// The date arithmetic and tree shape behind the history page. No imports from the app on purpose: it is tested on its
// own with node --test, and nothing here knows about React or the API client.

const DAY_MS = 24 * 60 * 60 * 1000

export type ChartDepartment = {
	id: string
	parentId: string | null
	name: string
	identifier: string
	depth: number
	people: { id: string; name: string; position: string }[]
}

export type TreeNode = ChartDepartment & { children: TreeNode[] }

function startOfUtcDay(value: string | Date): number {
	const date = typeof value === 'string' ? new Date(value) : value
	return Date.UTC(date.getUTCFullYear(), date.getUTCMonth(), date.getUTCDate())
}

// How many days the slider spans: from the day of the first recorded event to the day of `now`, both included as steps.
// Nothing recorded (or a start in the future) leaves a slider with nothing to move over.
export function daySpan(firstEventAt: string | null, now: Date): number {
	if (!firstEventAt) return 0
	return Math.max(0, Math.round((startOfUtcDay(now) - startOfUtcDay(firstEventAt)) / DAY_MS))
}

// The bare date (yyyy-MM-dd, UTC) at a slider position. The API reads a bare date as the end of that UTC day, so what
// the person sees for a position is what the org looked like when that day was over.
export function dateAtOffset(firstEventAt: string, offset: number): string {
	const day = new Date(startOfUtcDay(firstEventAt) + Math.max(0, offset) * DAY_MS)
	return day.toISOString().slice(0, 10)
}

export function offsetOfDate(firstEventAt: string, date: string): number {
	return Math.round((startOfUtcDay(date) - startOfUtcDay(firstEventAt)) / DAY_MS)
}

// Departments arrive flat, shallowest first. A department whose parent is not in the list (the API already turns a
// missing parent into a root) is a root here too, so nothing is dropped by a broken chain.
export function buildTree(departments: ChartDepartment[]): TreeNode[] {
	const byId = new Map<string, TreeNode>()
	for (const department of departments) byId.set(department.id, { ...department, children: [] })

	const roots: TreeNode[] = []
	for (const node of byId.values()) {
		const parent = node.parentId ? byId.get(node.parentId) : undefined
		if (parent && parent !== node) parent.children.push(node)
		else roots.push(node)
	}
	return roots
}

export function headcount(node: TreeNode): number {
	return node.people.length + node.children.reduce((sum, child) => sum + headcount(child), 0)
}
