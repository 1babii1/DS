import { ArrowUpRight, CircleCheck, CircleOff } from 'lucide-react'
import Link from 'next/link'

import type { ParentDepartment } from '@/entities/departments/types/department.types'

export default function DepartmentCard({ department }: { department: ParentDepartment }) {
	return (
		<article className='department-card'>
			<div className='department-card__header'>
				<span className='department-card__state' data-active={department.isActive}>
					{department.isActive ? <CircleCheck aria-hidden='true' size={16} /> : <CircleOff aria-hidden='true' size={16} />}
					{department.isActive ? 'Active' : 'Inactive'}
				</span>
				<Link aria-label={`View ${department.name}`} className='department-card__link' href={`/departments/${department.id}`}><ArrowUpRight aria-hidden='true' size={17} /></Link>
			</div>
			<h2>{department.name}</h2>
			<dl>
				<div><dt>Path</dt><dd>/{department.path}</dd></div>
				<div><dt>Last updated</dt><dd>{new Intl.DateTimeFormat('en', { dateStyle: 'medium' }).format(new Date(department.updatedAt))}</dd></div>
			</dl>
		</article>
	)
}
