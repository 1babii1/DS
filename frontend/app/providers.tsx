'use client'

import { QueryClientProvider } from '@tanstack/react-query'
import { ThemeProvider } from 'next-themes'
import type { ReactNode } from 'react'

import { queryClient } from '@/shared/api/query-client'

export function Providers({ children }: { children: ReactNode }) {
	return (
		<ThemeProvider attribute='class' defaultTheme='dark' enableSystem={false}>
			<QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
		</ThemeProvider>
	)
}
