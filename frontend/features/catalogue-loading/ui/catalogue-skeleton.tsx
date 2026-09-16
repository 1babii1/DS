import { Skeleton } from '@/shared/ui/skeleton'

export function CatalogueSkeleton({ kind = 'catalogue' }: { kind?: 'catalogue' | 'people' | 'organization' }) {
	const gridClass = kind === 'organization' ? 'department-grid' : kind === 'people' ? 'people-grid' : 'catalog-grid'
	return <section aria-busy='true' aria-label='Loading directory records' className={gridClass}>{Array.from({ length: 6 }, (_, index) => <article className='loading-card' key={index}><Skeleton className='h-5 w-10' /><Skeleton className='mt-8 h-5 w-3/5' /><Skeleton className='mt-3 h-3 w-4/5' /><Skeleton className='mt-auto h-3 w-2/5' /></article>)}</section>
}
