'use client'

import { SidebarProvider, SidebarTrigger } from '@/shared/components/ui/sidebar'
import { QueryClientProvider } from '@tanstack/react-query'
import { Toaster } from 'sonner'
import React from 'react'
import SidebarComponent from '../sidebar/sidebar.component'
import { queryClient } from '@/shared/api/query-client'

export default function Layout({ children }: { children: React.ReactNode }) {
	const [isOpen, setIsOpen] = React.useState(false)

	return (
		<div className='w-screen h-screen bg-[#3d3c3c] dark:bg-gray-900 p-10'>
			<QueryClientProvider client={queryClient}>
				<SidebarProvider open={isOpen} onOpenChange={setIsOpen}>
					<Toaster position='top-right' richColors />
					<div className='flex w-full h-full'>
						<aside className='overflow-hidden border-r border-gray-200 bg-white dark:bg-gray-800 dark:border-gray-700'>
							<SidebarComponent />
						</aside>
						<SidebarTrigger className='mt-2' />
						<main
							className={`flex-1 overflow-auto transition-all duration-200 ease-linear ${isOpen ? 'ml-56' : 'ml-16'}`}
						>
							{children}
						</main>
					</div>
				</SidebarProvider>
			</QueryClientProvider>
		</div>
	)
}
