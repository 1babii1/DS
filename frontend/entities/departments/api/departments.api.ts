import { axiosInstance } from '@/shared/api/axiosInstance'

import type { Department, GetChildrenLazyParams, GetParentDepartmentsParams, ParentDepartment } from '../types/department.types'

const departmentsPath = '/api/departments'

export const departmentsApi = {
	getDepartmentsTopPosition: async () => (await axiosInstance.get<Department[]>(`${departmentsPath}/top-positions`)).data,
	getDepartment: async (id: string) => (await axiosInstance.get<ParentDepartment>(`${departmentsPath}/department/${id}`)).data,
	getParentDepartments: async (params?: GetParentDepartmentsParams) => (await axiosInstance.get<ParentDepartment[]>(`${departmentsPath}/roots`, {
		params: { page: params?.page ?? 1, size: params?.size ?? 20, preferch: params?.preferch ?? 3 }
	})).data,
	getChildrenLazy: async (id: string, params?: GetChildrenLazyParams) => (await axiosInstance.get<ParentDepartment[]>(`${departmentsPath}/${id}/children`, {
		params: { page: params?.page ?? 1, size: params?.size ?? 20 }
	})).data,
	createDepartment: async (data: Partial<Department>) => (await axiosInstance.post<Department>(departmentsPath, data)).data
}
