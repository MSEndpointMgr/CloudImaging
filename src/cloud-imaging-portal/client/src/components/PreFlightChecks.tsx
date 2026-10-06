import { CheckCircle2, Minus, ShieldCheck, XCircle } from 'lucide-react';
import { Tooltip } from './ui/tooltip.tsx';
import { cn } from '../lib/utils.ts';
import {
  PRE_FLIGHT_ICONS,
  orderedChecks,
  preFlightCheckName,
  preFlightChipLabel,
  preFlightFixHint,
  preFlightValue,
  type PreFlightCheckOutcome,
  type PreFlightCheckResult,
} from '../lib/preflight.ts';

const CHIP_TONE: Record<PreFlightCheckOutcome, string> = {
  Passed: 'bg-emerald-500/10 text-emerald-600 dark:text-emerald-400',
  Failed: 'bg-destructive/15 text-destructive',
  Approved: 'bg-amber-500/15 text-amber-600 dark:text-amber-400',
  NotRequired: 'bg-muted text-muted-foreground',
};

function CheckChip({ result, size = 'md' }: { result: PreFlightCheckResult; size?: 'sm' | 'md' }): React.ReactElement {
  const Icon = PRE_FLIGHT_ICONS[result.check];
  return (
    <span
      className={cn(
        'inline-flex shrink-0 items-center justify-center rounded-md',
        size === 'md' ? 'h-7 w-7' : 'h-6 w-6',
        CHIP_TONE[result.outcome],
      )}
    >
      <Icon className="h-4 w-4" aria-hidden="true" />
    </span>
  );
}

/**
 * One icon chip per check, in display order. Colour carries the outcome; the tooltip and the
 * accessible name carry the check name and what the device reported.
 */
export function PreFlightCheckStrip({ checks }: { checks: readonly PreFlightCheckResult[] | null | undefined }): React.ReactElement {
  const ordered = orderedChecks(checks);
  if (ordered.length === 0) return <span className="text-muted-foreground">-</span>;
  return (
    <span className="inline-flex gap-1">
      {ordered.map(result => {
        const label = preFlightChipLabel(result);
        return (
          <Tooltip key={result.check} content={label}>
            <span
              role="img"
              aria-label={label}
              tabIndex={0}
              className="inline-flex rounded-md focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background"
            >
              <CheckChip result={result} />
            </span>
          </Tooltip>
        );
      })}
    </span>
  );
}

function StatusIcon({ outcome }: { outcome: PreFlightCheckOutcome }): React.ReactElement {
  switch (outcome) {
    case 'Passed':
      return <CheckCircle2 className="h-4 w-4 text-emerald-600 dark:text-emerald-400" role="img" aria-label="Passed" />;
    case 'Failed':
      return <XCircle className="h-4 w-4 text-destructive" role="img" aria-label="Failed" />;
    case 'Approved':
      return <ShieldCheck className="h-4 w-4 text-amber-600 dark:text-amber-400" role="img" aria-label="Approved" />;
    default:
      return (
        <Tooltip content="Not required">
          <Minus className="h-4 w-4 text-muted-foreground" role="img" aria-label="Not required" />
        </Tooltip>
      );
  }
}

/** Expanded-row view of the checks: one tile per check with what the device reported. */
export function PreFlightCheckTiles({ checks }: { checks: readonly PreFlightCheckResult[] }): React.ReactElement {
  return (
    <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-4">
      {orderedChecks(checks).map(result => {
        const notRequired = result.outcome === 'NotRequired';
        return (
          <div
            key={result.check}
            data-outcome={result.outcome}
            className={cn(
              'min-w-0 rounded-lg border p-3',
              result.outcome === 'Failed' && 'border-destructive/45 bg-destructive/5',
              result.outcome === 'Approved' && 'border-amber-500/45 bg-amber-500/5',
              (result.outcome === 'Passed' || notRequired) && 'border-border bg-background',
            )}
          >
            <div className="mb-2 flex items-center justify-between gap-2">
              <span className="inline-flex min-w-0 items-center gap-2">
                <CheckChip result={result} size="sm" />
                <span className="truncate text-xs text-muted-foreground">{preFlightCheckName(result.check)}</span>
              </span>
              <StatusIcon outcome={result.outcome} />
            </div>
            <p className={cn('text-sm font-medium', notRequired && 'text-muted-foreground')}>
              {preFlightValue(result.observed)}
            </p>
            {result.outcome === 'Failed' && (
              <p className="mt-1 text-xs text-amber-600 dark:text-amber-400">{preFlightFixHint(result.check, result.observed)}</p>
            )}
            {result.outcome === 'Approved' && result.approvedBy && (
              <p className="mt-1 truncate text-xs text-muted-foreground">Approved by {result.approvedBy}</p>
            )}
          </div>
        );
      })}
    </div>
  );
}
