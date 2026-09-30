export type OrgPerson = { id: string; name: string; position: string }

export type OrgDepartment = {
	id: string
	parentId: string | null
	name: string
	identifier: string
	depth: number
	people: OrgPerson[]
}

// The org as it was at an instant, rebuilt by the audit service from its event log.
export type OrgChart = {
	at: string
	firstEventAt: string | null
	departments: OrgDepartment[]
	unplaced: OrgPerson[]
	skippedEvents: number
}
