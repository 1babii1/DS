import { axiosInstance } from '@/shared/api/axiosInstance'
import {
	CreateDepartmentRequest,
	Department,
	GetChildrenLazyParams,
	GetDepartmentBySearchParams,
	GetParentDepartmentsParams,
	ParentDepartment,
	UpdateParentRequest
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
	CreateDepartment: async (data: CreateDepartmentRequest) => {
		const transformedData = {
			request: {
				name: { value: data.name },
				identifier: { value: data.identifier },
				parentDepartmentId: data.parentDepartmentId
					? { value: data.parentDepartmentId }
					: null,
				depth: data.depth ?? 0,
				locationsIds: data.locationsIds.map(id => ({ value: id })),
				departmentId: null
			}
		}
		const response = await axiosInstance.post<Department>(
			'http://localhost:5129/api/departments',
			transformedData
		)

		return response
	},
	GetDepartmentBySearch: async (params: GetDepartmentBySearchParams) => {
		const response = await axiosInstance
			.get<Department[]>('http://localhost:5129/search', {
				params: {
					search: params.search,
					page: params.page ?? 1,
					size: params.size ?? 10
				}
			})
			.then(res => res.data)
			.catch(() => [])
		return response
	},
	DeleteDepartment: async (id: string) =>
		await axiosInstance.delete(
			`http://localhost:5129/api/departments/${id}`
		),

	UpdateParent: async (data: UpdateParentRequest) =>
		await axiosInstance.put(
			`http://localhost:5129/api/departments/${data.departmentId}`,
			data.parentDepartmentId
		)
}
