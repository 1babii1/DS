import { axiosInstance } from '@/shared/api/axiosInstance'
import type { Employee, EmployeeCard, EmployeePage } from '../types/employee.types'

export type HireEmployeeInput = { fullName: string; email: string; departmentId: string; positionId: string }
export type TransferEmployeeInput = Pick<HireEmployeeInput, 'departmentId' | 'positionId'>

export const employeesApi = {
	list: async (departmentId?: string, page = 1) => (await axiosInstance.get<EmployeePage>('/api/employees', { params: { page, ...(departmentId ? { departmentId } : {}) } })).data,
	get: async (employeeId: string) => (await axiosInstance.get<Employee>(`/api/employees/${employeeId}`)).data,
	card: async (employeeId: string, minWalletVersion?: number) => (await axiosInstance.get<EmployeeCard>(`/api/employees/${employeeId}/card`, { headers: minWalletVersion ? { 'X-Min-Wallet-Version': String(minWalletVersion) } : undefined })).data,
	hire: async (input: HireEmployeeInput, idempotencyKey: string) => (await axiosInstance.post<string>('/api/employees', input, { headers: { 'Idempotency-Key': idempotencyKey } })).data,
	transfer: async (employeeId: string, input: TransferEmployeeInput) => axiosInstance.put(`/api/employees/${employeeId}/transfer`, input)
}
