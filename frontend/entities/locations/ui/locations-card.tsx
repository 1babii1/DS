import { Button } from '@/shared/components/ui/button'
import {
	Card,
	CardContent,
	CardDescription,
	CardFooter,
	CardHeader,
	CardTitle
} from '@/shared/components/ui/card'
import { Link } from 'lucide-react'
import React from 'react'
import { Location } from '../types/location-types'

export default function Cardlocation({
	location
}: {
	location: Location
}): React.JSX.Element {
	return (
		<div className='h-64 w-full'>
			<Card className='flex h-full flex-col'>
				<CardHeader>
					<CardTitle className='line-clamp-2'>
						<div className='flex flex-row justify-between'>
							{location.name ?? 'Неизвестно'}
							<Button>
								<Link href={`/departments/${location.id}`}>
									Просмотр
								</Link>
							</Button>
						</div>
					</CardTitle>

					<CardDescription>
						Статус: {location.isActive ? 'Активен' : 'Неактивен'}
					</CardDescription>
				</CardHeader>
				<CardContent className='flex-1 overflow-y-auto'>
					<div className='space-y-2 text-sm'>
						<p>
							<span className='font-semibold'>Страна: </span>
							{location.country
								? location.country
								: 'Корневой департамент'}
						</p>
						<p>
							<span className='font-semibold'>Адрес: </span>
							{location.city}, {location.street}
						</p>
						<p>
							<span className='font-semibold'>
								Часовой пояс:{' '}
							</span>
							{location.timezone}
						</p>
					</div>
				</CardContent>
				<CardFooter className='border-t pt-4'>
					<p className='text-xs text-muted-foreground'>
						Обновлено {location.updatedAt.toLocaleString()}
					</p>
				</CardFooter>
			</Card>
		</div>
	)
}
