export type Employee = { id: string; fullName: string; email: string; departmentId: string; departmentName: string; positionId: string; positionName: string; status: string; hiredAt: string }
export type EmployeePage = { items: Employee[]; page: number; size: number; total: number; hasNext: boolean }
