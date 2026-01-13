import React from 'react'
import { Home, Inbox } from 'lucide-react'
import {
	Sidebar,
	SidebarContent,
	SidebarGroup,
	SidebarGroupContent,
	SidebarGroupLabel,
	SidebarHeader,
	SidebarMenu,
	SidebarMenuButton,
	SidebarMenuItem,
	SidebarTrigger
} from '@/components/ui/sidebar'
import Link from 'next/link'

export default function SidebarComponent() {
	const items = [
		{
			title: 'Home',
			url: '/',
			icon: Home
		},
		{
			title: 'Departments',
			url: '/departments',
			icon: Inbox
		}
	]
	return (
		<div>
			<Sidebar side='left'>
				<SidebarHeader>
					<div>
						DirectoryService
						<SidebarTrigger />
					</div>
				</SidebarHeader>
				<SidebarContent>
					<SidebarGroup>
						<SidebarGroupContent>
							<SidebarMenu>
								{items.map(item => (
									<SidebarMenuItem key={item.title}>
										<SidebarMenuButton asChild>
											<Link href={item.url}>
												<item.icon />
												<span>{item.title}</span>
											</Link>
										</SidebarMenuButton>
									</SidebarMenuItem>
								))}
							</SidebarMenu>
						</SidebarGroupContent>
					</SidebarGroup>
				</SidebarContent>
			</Sidebar>
		</div>
	)
}
