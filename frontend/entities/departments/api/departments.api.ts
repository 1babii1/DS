import { axiosInstance } from '@/shared/api/axiosInstance'
import {
	Department,
	GetChildrenLazyParams,
	GetParentDepartmentsParams,
	ParentDepartment
} from '../types/department.types'

export const departmentsApi = {
	getDepartmentsTopPosition: async () => {
		const response = await axiosInstance
			.get<Department[]>('http://localhost:5129/top-positions')
			.then(res => res.data)
			.catch(() => [])
		return response
	},
	getDepartment: async (id: string) => {
		const response = await axiosInstance
			.get<ParentDepartment>(`http://localhost:5129/department/${id}`)
			.then(res => res.data)
			.catch(() => null)
		return response
	},
	getParentDepartments: async (params?: GetParentDepartmentsParams) => {
		const response = await axiosInstance
			.get<ParentDepartment[]>('http://localhost:5129/roots', {
				params: {
					page: params?.page ?? 1,
					size: params?.size ?? 20,
					preferch: params?.preferch ?? 3
				}
			})
			.then(res => res.data)
			.catch(() => [])
		return response
	},
	getChildrenLazy: async (id: string, params?: GetChildrenLazyParams) => {
		const response = await axiosInstance
			.get<ParentDepartment[]>(`http://localhost:5129/${id}/children`, {
				params: {
					page: params?.page ?? 1,
					size: params?.size ?? 20
				}
			})
			.then(res => res.data)
			.catch(() => [])
		return response
	},
	CreateDepartmernt: async (data: Partial<Department>) => {
		const response = await axiosInstance
			.post<Department>('http://localhost:5129/department', data)
			.then(res => res.data)
			.catch(() => null)
		return response
	}
}
