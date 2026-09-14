'use client'

import { zodResolver } from '@hookform/resolvers/zod'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { LoaderCircle } from 'lucide-react'
import { useEffect } from 'react'
import { useForm, useWatch } from 'react-hook-form'
import { z } from 'zod'

import { applyEnvelopeErrors } from '@/shared/api/validation-error'
import { useNotice } from '@/shared/ui/notice-provider'
import { mutationErrorMessage } from '@/shared/api/mutation-error'
import { employeesApi } from '@/entities/employees/api/employees.api'
import type { Position } from '@/entities/directory/types/catalog.types'

const employeeSchema = z.object({ fullName: z.string().trim().optional(), email: z.string().trim().optional(), departmentId: z.string().uuid('Choose a department.'), positionId: z.string().uuid('Choose a position.') })
type EmployeeFormValues = z.infer<typeof employeeSchema>
type DepartmentOption = { id: string; name: string }

function Field({ children, error, label }: { children: React.ReactNode; error?: string; label: string }) { return <label className='form-field'><span>{label}</span>{children}{error ? <small role='alert'>{error}</small> : null}</label> }

export function EmployeeForm({ employee, onClose, positions }: { employee?: { id: string; name: string }; onClose: () => void; positions: Position[] }) {
	const queryClient = useQueryClient()
	const { showSuccess } = useNotice()
	const departments = Array.from(new Map(positions.flatMap(position => position.departments).map(department => [department.id, { id: department.id, name: department.name }])).values()).sort((a, b) => a.name.localeCompare(b.name)) as DepartmentOption[]
	const form = useForm<EmployeeFormValues>({ resolver: zodResolver(employeeSchema), mode: 'onSubmit', defaultValues: { fullName: '', email: '', departmentId: '', positionId: '' } })
	useEffect(() => { form.setFocus(employee ? 'departmentId' : 'fullName') }, [employee, form])
	const departmentId = useWatch({ control: form.control, name: 'departmentId', defaultValue: '' })
	const availablePositions = positions.filter(position => position.departments.some(department => department.id === departmentId))
	useEffect(() => { form.setValue('positionId', '') }, [departmentId, form])
	const mutation = useMutation({ mutationFn: async (values: EmployeeFormValues) => employee ? employeesApi.transfer(employee.id, values) : employeesApi.hire({ ...values, fullName: values.fullName ?? '', email: values.email ?? '' }), onSuccess: async () => { await queryClient.invalidateQueries({ queryKey: ['employees'] }); showSuccess(employee ? 'Employee transfer confirmed.' : 'Employee hired.'); onClose() }, onError: error => { if (!applyEnvelopeErrors(error, ['fullName', 'email', 'departmentId', 'positionId'], form.setError)) form.setError('root', { message: mutationErrorMessage(error, 'The request') }) } })
	const submit = (values: EmployeeFormValues) => { if (!employee && (!values.fullName || values.fullName.length < 2 || !values.email || !z.string().email().safeParse(values.email).success)) { form.setError('root', { message: 'Enter a full name and a valid work email.' }); return } mutation.mutate(values) }
	return <section aria-labelledby='employee-form-title' className='employee-form-panel'><div className='employee-form-panel__heading'><div><p className='eyebrow'>{employee ? 'Employee transfer' : 'New employee'}</p><h2 id='employee-form-title'>{employee ? `Transfer ${employee.name}` : 'Hire employee'}</h2></div><button aria-label='Close form' className='icon-button' onClick={onClose} type='button'>×</button></div><form className='employee-form' onSubmit={form.handleSubmit(submit)}>{!employee ? <><Field error={form.formState.errors.fullName?.message} label='Full name'><input {...form.register('fullName')} autoComplete='name' placeholder='Alex Morgan' /></Field><Field error={form.formState.errors.email?.message} label='Work email'><input {...form.register('email')} autoComplete='email' placeholder='alex@company.com' type='email' /></Field></> : null}<Field error={form.formState.errors.departmentId?.message} label='Department'><select {...form.register('departmentId')}><option value=''>Choose department</option>{departments.map(department => <option key={department.id} value={department.id}>{department.name}</option>)}</select></Field><Field error={form.formState.errors.positionId?.message} label='Position'><select {...form.register('positionId')} disabled={!departmentId}><option value=''>{departmentId ? 'Choose position' : 'Choose department first'}</option>{availablePositions.map(position => <option key={position.id} value={position.id}>{position.name}</option>)}</select></Field>{form.formState.errors.root ? <p className='form-error' role='alert'>{form.formState.errors.root.message}</p> : null}<button className='form-submit' disabled={mutation.isPending} type='submit'>{mutation.isPending ? <LoaderCircle aria-hidden='true' className='animate-spin' size={16} /> : null}{employee ? 'Confirm transfer' : 'Hire employee'}</button></form></section>
}
