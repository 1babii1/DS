import { Activity } from 'lucide-react'
import { WorkspaceState } from '@/features/workspace-state/ui/workspace-state'
export default function ActivityPage() { return <WorkspaceState description='Audit events will make changes to organization data traceable from the moment they are recorded.' icon={Activity} label='Audit trail' title='Activity' /> }
