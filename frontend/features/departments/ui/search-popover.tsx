import {
	Command,
	CommandGroup,
	CommandInput,
	CommandItem,
	CommandList
} from '@/shared/components/ui/command'
import {
	Popover,
	PopoverContent,
	PopoverTrigger
} from '@/shared/components/ui/popover'
import { Loader2 } from 'lucide-react'
import { ReactNode, useState } from 'react'

interface SearchPopoverProps<T> {
	isLoading: boolean
	isFetching: boolean
	items: T[]
	searchValue: string
	onSearchChange: (value: string) => void
	placeholder: string
	trigger: ReactNode
	onSelect: (item: T) => void
	renderItem: (item: T) => ReactNode
	getItemId: (item: T) => string
	emptyMessage?: string
	onOpenChange?: (open: boolean) => void
	isSingleSelect?: boolean
}

export function SearchPopover<T>({
	isFetching,
	items,
	searchValue,
	onSearchChange,
	placeholder,
	trigger,
	onSelect,
	renderItem,
	getItemId,
	emptyMessage = 'No items found.',
	onOpenChange,
	isSingleSelect = false
}: SearchPopoverProps<T>) {
	const [open, setOpen] = useState(false)

	const handleSelect = (item: T) => {
		onSelect(item)
		if (isSingleSelect) {
			setOpen(false)
			onOpenChange?.(false)
			onSearchChange('')
		}
	}

	return (
		<Popover open={open} onOpenChange={setOpen}>
			<PopoverTrigger asChild>{trigger}</PopoverTrigger>
			<PopoverContent className='w-75 p-0'>
				<Command shouldFilter={false}>
					<CommandInput
						placeholder={placeholder}
						value={searchValue}
						onValueChange={onSearchChange}
					/>
					{isFetching ? (
						<div className='flex items-center justify-center p-4'>
							<Loader2 className='h-4 w-4 animate-spin text-muted-foreground' />
						</div>
					) : items.length > 0 ? (
						<CommandList>
							<CommandGroup key={`items-${items.length}`}>
								{items.map(item => (
									<CommandItem
										key={getItemId(item)}
										onSelect={() => handleSelect(item)}
									>
										{renderItem(item)}
									</CommandItem>
								))}
							</CommandGroup>
						</CommandList>
					) : (
						<div className='p-4 text-sm text-muted-foreground'>
							{emptyMessage}
						</div>
					)}
				</Command>
			</PopoverContent>
		</Popover>
	)
}
