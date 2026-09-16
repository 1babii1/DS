'use client'
import { Search } from 'lucide-react'
import { usePathname, useRouter, useSearchParams } from 'next/navigation'
import { useEffect, useState } from 'react'
export function AggregateFilter(){const router=useRouter();const path=usePathname();const params=useSearchParams();const[value,setValue]=useState(params.get('aggregateId')??'');useEffect(()=>{const id=window.setTimeout(()=>{const next=new URLSearchParams(params);if(value.trim())next.set('aggregateId',value.trim());else next.delete('aggregateId');next.delete('page');router.replace(`${path}?${next.toString()}`)},250);return()=>window.clearTimeout(id)},[value,params,path,router]);return <label className='aggregate-filter'><span>Filter by aggregate ID</span><div><Search aria-hidden='true' size={16}/><input aria-label='Filter by aggregate ID' onChange={e=>setValue(e.target.value)} placeholder='Employee or department ID' value={value}/></div></label>}
