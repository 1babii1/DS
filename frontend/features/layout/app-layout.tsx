'use client'

import { SidebarProvider, SidebarTrigger } from '@/components/ui/sidebar'
import { QueryClientProvider } from '@tanstack/react-query'
import React from 'react'
import SidebarComponent from '../sidebar/sidebar.component'
import { queryClient } from '@/shared/api/query-client'

export default function Layout({ children }: { children: React.ReactNode }) {
	const [isOpen, setIsOpen] = React.useState(false)

	return (
		<div className='bg-gray-500'>
			<QueryClientProvider client={queryClient}>
				<SidebarProvider open={isOpen} onOpenChange={setIsOpen}>
					<div>
						<aside className='overflow-hidden border-r border-gray-200 bg-white dark:bg-gray-800 dark:border-gray-700'>
							<SidebarComponent />
						</aside>
						<SidebarTrigger className='mt-2' />
						<main
							className={`flex-1 transition-margin duration-200 ease-linear ${isOpen ? 'ml-45' : 'ml-10'}`}
						>
							{children}
						</main>
					</div>
				</SidebarProvider>
			</QueryClientProvider>
		</div>
	)
}
