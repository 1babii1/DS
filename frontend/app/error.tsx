'use client'

import { RefreshCw, TriangleAlert } from 'lucide-react'

export default function GlobalError({ error, reset }: { error: Error & { digest?: string }; reset: () => void }) {
	return <div className='page'><section aria-describedby={error.digest ? 'workspace-load-error' : undefined} className='route-state' role='alert'><div className='empty-state__icon'><TriangleAlert aria-hidden='true' size={21}/></div><p className='eyebrow'>Something needs attention</p><h1>We could not load this workspace.</h1><p id={error.digest ? 'workspace-load-error' : undefined}>The request did not complete as expected. You can safely try it again.</p><button className='retry-button' onClick={reset} type='button'><RefreshCw aria-hidden='true' size={16}/>Try again</button></section></div>
}
