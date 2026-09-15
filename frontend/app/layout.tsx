import type { Metadata } from 'next'
import { Geist, Geist_Mono } from 'next/font/google'

import './globals.css'
import { auth } from '@/auth'
import { AppShell } from '@/features/app-shell/ui/app-shell'
import { SessionControl } from '@/features/auth/ui/session-control'
import { Providers } from './providers'

const geistSans = Geist({ variable: '--font-geist-sans', subsets: ['latin'] })
const geistMono = Geist_Mono({ variable: '--font-geist-mono', subsets: ['latin'] })

export const metadata: Metadata = {
	title: { default: 'DS — People & Organization', template: '%s · DS' },
	description: 'A people and organization workspace built on a distributed services platform.'
}

export const dynamic = 'force-dynamic'

export default async function RootLayout({ children }: Readonly<{ children: React.ReactNode }>) {
	const session = await auth()
	return (
		<html lang='en' suppressHydrationWarning>
			<body className={`${geistSans.variable} ${geistMono.variable}`}>
				<Providers><AppShell accountControl={<SessionControl user={session?.user} />}>{children}</AppShell></Providers>
			</body>
		</html>
	)
}
