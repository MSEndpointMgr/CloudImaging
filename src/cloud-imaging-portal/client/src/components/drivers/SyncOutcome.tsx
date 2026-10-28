import React from 'react';
import { CircleArrowUp, CircleMinus, CirclePlus, CircleX, Clock } from 'lucide-react';
import { Tooltip } from '../ui/tooltip.tsx';
import { outcomeLabel, type DriverSyncItemOutcome } from '../../lib/drivers.ts';

const ICONS: Record<DriverSyncItemOutcome, { icon: typeof CircleX; className: string }> = {
  Updated: { icon: CircleArrowUp, className: 'text-blue-600 dark:text-blue-400' },
  New: { icon: CirclePlus, className: 'text-emerald-600 dark:text-emerald-400' },
  NewWaitingForApproval: { icon: Clock, className: 'text-amber-600 dark:text-amber-400' },
  Withdrawn: { icon: CircleMinus, className: 'text-muted-foreground' },
  Removed: { icon: CircleMinus, className: 'text-muted-foreground' },
  Failed: { icon: CircleX, className: 'text-destructive' },
};

/** Icon and label of a sync item's outcome; a failure reason is the icon's tooltip. */
export function SyncOutcome({ outcome, reason }: { outcome: DriverSyncItemOutcome; reason?: string | null }): React.ReactElement {
  const { icon: Icon, className } = ICONS[outcome];
  const label = outcomeLabel(outcome);
  return (
    <span className="flex min-w-0 items-center gap-2">
      {reason ? (
        <Tooltip content={reason}>
          <span tabIndex={0} aria-label={`${label}: ${reason}`} className="inline-flex shrink-0">
            <Icon className={`h-4 w-4 ${className}`} aria-hidden="true" />
          </span>
        </Tooltip>
      ) : (
        <Icon className={`h-4 w-4 shrink-0 ${className}`} aria-hidden="true" />
      )}
      <span className="truncate">{label}</span>
    </span>
  );
}
