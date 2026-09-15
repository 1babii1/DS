import { axiosInstance } from '@/shared/api/axiosInstance'

import type { Department, GetChildrenLazyParams, GetParentDepartmentsParams, ParentDepartment } from '../types/department.types'

const departmentsPath = '/api/departments'

export type CreateDepartmentInput = { name: string; identifier: string; parentDepartmentId?: string; locationsIds: string[] }

export const departmentsApi = {
	getDepartmentsTopPosition: async () => (await axiosInstance.get<Department[]>(`${departmentsPath}/top-positions`)).data,
	getDepartment: async (id: string) => (await axiosInstance.get<ParentDepartment>(`${departmentsPath}/department/${id}`)).data,
	getParentDepartments: async (params?: GetParentDepartmentsParams) => (await axiosInstance.get<ParentDepartment[]>(`${departmentsPath}/roots`, {
		params: { page: params?.page ?? 1, size: params?.size ?? 20, preferch: params?.preferch ?? 3 }
	})).data,
	getChildrenLazy: async (id: string, params?: GetChildrenLazyParams) => (await axiosInstance.get<ParentDepartment[]>(`${departmentsPath}/${id}/children`, {
		params: { page: params?.page ?? 1, size: params?.size ?? 20 }
	})).data,
	createDepartment: async (input: CreateDepartmentInput) => (await axiosInstance.post<string>(departmentsPath, { request: { name: input.name, identifier: input.identifier, parentDepartmentId: input.parentDepartmentId || null, depth: null, locationsIds: input.locationsIds, departmentId: null } })).data
}
