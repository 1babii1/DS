import { axiosInstance } from '@/shared/api/axiosInstance'
import type { Employee } from '../types/employee.types'

export type HireEmployeeInput = { fullName: string; email: string; departmentId: string; positionId: string }
export type TransferEmployeeInput = Pick<HireEmployeeInput, 'departmentId' | 'positionId'>

export const employeesApi = {
	list: async (departmentId?: string) => (await axiosInstance.get<Employee[]>('/api/employees', { params: departmentId ? { departmentId } : undefined })).data,
	hire: async (input: HireEmployeeInput) => (await axiosInstance.post<string>('/api/employees', input)).data,
	transfer: async (employeeId: string, input: TransferEmployeeInput) => axiosInstance.put(`/api/employees/${employeeId}/transfer`, input)
}
