'use client'

import { AlertCircle, Check, X } from 'lucide-react'
import { createContext, useCallback, useContext, useEffect, useMemo, useState } from 'react'
import type { ReactNode } from 'react'

type Notice = { id: number; message: string; tone: 'error' | 'success' }
type NoticeContextValue = { showError: (message: string) => void; showSuccess: (message: string) => void }

const NoticeContext = createContext<NoticeContextValue | null>(null)

export function NoticeProvider({ children }: { children: ReactNode }) {
	const [notice, setNotice] = useState<Notice | null>(null)

	useEffect(() => {
		if (!notice) return
		const timeout = window.setTimeout(() => setNotice(null), 5000)
		return () => window.clearTimeout(timeout)
	}, [notice])

	const showSuccess = useCallback((message: string) => {
		setNotice({ id: Date.now(), message, tone: 'success' })
	}, [])
	const showError = useCallback((message: string) => {
		setNotice({ id: Date.now(), message, tone: 'error' })
	}, [])
	const value = useMemo(() => ({ showError, showSuccess }), [showError, showSuccess])

	return (
		<NoticeContext.Provider value={value}>
			{children}
			{notice ? <div aria-atomic='true' aria-live='polite' className={notice.tone === 'error' ? 'success-notice success-notice--error' : 'success-notice'} role={notice.tone === 'error' ? 'alert' : 'status'}>
				{notice.tone === 'error' ? <AlertCircle aria-hidden='true' size={17} /> : <Check aria-hidden='true' size={17} />}
				<span>{notice.message}</span>
				<button aria-label='Dismiss notification' onClick={() => setNotice(null)} type='button'><X aria-hidden='true' size={16} /></button>
			</div> : null}
		</NoticeContext.Provider>
	)
}

export function useNotice() {
	const context = useContext(NoticeContext)
	if (!context) throw new Error('useNotice must be used within NoticeProvider')
	return context
}
