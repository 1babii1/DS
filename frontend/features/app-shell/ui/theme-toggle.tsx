'use client'

import { Moon, Sun } from 'lucide-react'
import { useTheme } from 'next-themes'

export function ThemeToggle() {
	const { resolvedTheme, setTheme } = useTheme()
	const isDark = resolvedTheme !== 'light'
	const label = isDark ? 'Switch to light theme' : 'Switch to dark theme'

	return (
		<button
			aria-label={label}
			className='icon-button'
			onClick={() => setTheme(isDark ? 'light' : 'dark')}
			type='button'
		>
			{isDark ? <Sun aria-hidden='true' size={18} /> : <Moon aria-hidden='true' size={18} />}
		</button>
	)
}
