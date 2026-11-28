import { ChevronDown, ChevronRight } from 'lucide-react';
import { TableHead } from './table.tsx';
import { cn } from '../../lib/utils.ts';

/**
 * First column of a table whose rows expand. It is only as wide as the chevron plus the table's left
 * padding, so the chevron sits as far from the next column's text as from the table edge. Widen it
 * (`className="w-14"`) only when a status icon shares the cell with the chevron.
 */
export function RowToggleHead({ className }: { className?: string }): React.ReactElement {
  return <TableHead className={cn('w-9 pr-0', className)}><span className="sr-only">Details</span></TableHead>;
}

interface RowToggleProps {
  expanded: boolean;
  /** Names the action and the row, for example "Show details of System BIOS". */
  label: string;
  /** Id of the details row it reveals. */
  controls?: string;
  onToggle: () => void;
}

/**
 * The chevron that shows or hides a table row's details, in a `pr-0` cell under a
 * {@link RowToggleHead}. A raw button rather than `Button`: a disclosure toggle carries its own ARIA
 * contract, and its 24px footprint is what keeps the first column narrow.
 */
export function RowToggle({ expanded, label, controls, onToggle }: RowToggleProps): React.ReactElement {
  const Icon = expanded ? ChevronDown : ChevronRight;
  return (
    <button
      type="button"
      aria-expanded={expanded}
      aria-controls={controls}
      aria-label={label}
      className="inline-flex rounded-md p-1 text-muted-foreground transition-colors hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background"
      onClick={onToggle}
    >
      <Icon className="h-4 w-4" aria-hidden="true" />
    </button>
  );
}
