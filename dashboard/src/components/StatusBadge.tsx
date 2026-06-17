import { Badge } from '@/components/ui/badge'
import type { badgeVariants } from '@/components/ui/badge'
import type { VariantProps } from 'class-variance-authority'

type Variant = VariantProps<typeof badgeVariants>['variant']

// Maps the various status strings the control plane returns onto badge tones.
const STATUS_VARIANT: Record<string, Variant> = {
  // zones / nodes / certificates
  Active: 'success',
  Provisioning: 'warning',
  Registered: 'warning',
  Pending: 'warning',
  Paused: 'secondary',
  Disabled: 'secondary',
  Expired: 'secondary',
  Revoked: 'secondary',
  Error: 'destructive',
  Failed: 'destructive',
}

export function StatusBadge({ status }: { status: string }) {
  return <Badge variant={STATUS_VARIANT[status] ?? 'secondary'}>{status}</Badge>
}
