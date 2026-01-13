export type Department = {
	id: string
	parentId: string
	name: string
	identifier: string
	path: string
	depth: number
	isActive: boolean
	createdAt: Date
	updatedAt: Date
}

export type ParentDepartment = {
	id: string
	parentId: string
	name: string
	identifier: string
	path: string
	depth: number
	isActive: boolean
	createdAt: Date
	updatedAt: Date
	children: ParentDepartment[]
	hasMoreChildren: boolean
}

export interface GetParentDepartmentsParams {
	page?: number
	size?: number
	preferch?: number
}

export interface GetChildrenLazyParams {
	page?: number
	size?: number
}
