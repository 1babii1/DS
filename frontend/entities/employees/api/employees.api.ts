import { axiosInstance } from '@/shared/api/axiosInstance'
import type { Employee } from '../types/employee.types'
export const employeesApi = { list: async (departmentId?: string) => (await axiosInstance.get<Employee[]>('/api/employees', { params: departmentId ? { departmentId } : undefined })).data }
