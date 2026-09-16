import { ArrowLeft } from 'lucide-react'
import Link from 'next/link'
export default function NotFound() { return <div className='page'><section className='route-state'><p className='eyebrow'>404 · route not found</p><h1>This workspace route does not exist.</h1><p>Return to the overview to continue through the People & Organization workspace.</p><Link className='retry-button' href='/'><ArrowLeft aria-hidden='true' size={16}/>Back to overview</Link></section></div> }
