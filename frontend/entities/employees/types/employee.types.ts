export type Employee = { id: string; fullName: string; email: string; departmentId: string; departmentName: string; positionId: string; positionName: string; status: string; provisioningFailureReason: string | null; hiredAt: string }
export type EmployeePage = { items: Employee[]; page: number; size: number; total: number; hasNext: boolean }

// The employee with their wallet balance as the employee service holds it (ADR 0034). The balance is a copy that trails the wallet: `balanceAsOfVersion`
// and `balanceChangedAt` say how current it is, `balance` is null until the first wallet event has arrived, and `consistent` is false when the
// card was asked to wait for a wallet version and had not reached it when the wait ran out.
export type EmployeeCard = { employee: Employee; balance: number | null; balanceAsOfVersion: number | null; balanceChangedAt: string | null; consistent: boolean }
