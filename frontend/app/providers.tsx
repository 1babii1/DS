'use client'

import { QueryClientProvider } from '@tanstack/react-query'
import { ThemeProvider } from 'next-themes'
import type { ReactNode } from 'react'

import { queryClient } from '@/shared/api/query-client'
import { EditingCapabilityProvider } from '@/shared/auth/capabilities'
import { NoticeProvider } from '@/shared/ui/notice-provider'

export function Providers({ authenticated, children }: { authenticated: boolean; children: ReactNode }) {
	return (
		<ThemeProvider attribute='class' defaultTheme='dark' enableSystem={false}>
			<QueryClientProvider client={queryClient}><EditingCapabilityProvider authenticated={authenticated}><NoticeProvider>{children}</NoticeProvider></EditingCapabilityProvider></QueryClientProvider>
		</ThemeProvider>
	)
}
