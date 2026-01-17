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

export interface GetDepartmentBySearchParams {
	search: string
	page?: number
	size?: number
}

export interface CreateDepartmentRequest {
	name: string
	identifier: string
	parentDepartmentId?: string
	depth?: number
	locationsIds: string[]
}

export interface DepartmentForm {
	name: string
	identifier: string
	parentDepartmentId?: string
	depth?: number
	locationsIds: string[]
}

export interface DepartmentFetchForm {
	id: string
	name: string
	identifier?: string
}

export interface LocationForm {
	id: string
	name: string
	city?: string
}

export type FormField<TForm, TField extends keyof TForm> = {
	state: {
		value: TForm[TField]
		meta: {
			errors: { message?: string | undefined }[]
			isValid: boolean
			isTouched: boolean
		}
	}
	handleChange: (value: TForm[TField]) => void
	handleBlur: () => void
}

export type UpdateParentRequest = {
	departmentId: string
	parentDepartmentId: string
}
