import * as React from 'react';
import { X } from 'lucide-react';
import { Button } from './ui/button.tsx';

/** One removable filter shown as a chip under the bar. */
export interface AppliedFilter {
  /** Stable identity for the chip, e.g. the filter's field name. */
  key: string;
  /** Chip text, already including its field, e.g. "Outcome: Rejected". */
  label: string;
  onClear: () => void;
}

interface ReportFilterBarProps {
  /** Search field, date range and any additional selects, in that order. */
  children: React.ReactNode;
  /** Chips for whatever is currently narrowing the rows. Omit anything left at its default. */
  applied?: readonly AppliedFilter[];
  onClearAll?: () => void;
  /** Export control, kept at the end of the bar. */
  actions?: React.ReactNode;
}

/**
 * The filter bar shared by every report.
 *
 * Four report pages used to build this themselves, which is exactly why their labels, widths and
 * search icons disagreed. Controls carry their own value as their label, so the bar is one 32px
 * row rather than a 58px row of stacked labels, and the chip row only exists while something is
 * actually filtered.
 */
export function ReportFilterBar({
  children,
  applied = [],
  onClearAll,
  actions,
}: ReportFilterBarProps): React.ReactElement {
  return (
    <div className="space-y-3">
      <div className="flex flex-wrap items-center gap-2">
        {children}
        <div className="flex-1" />
        {actions}
      </div>

      {applied.length > 0 && (
        <div className="flex flex-wrap items-center gap-2">
          {applied.map(filter => (
            // A chip removes a filter, so it is a Button rather than a Badge: Badge is
            // cursor-default and carries no focus ring.
            <Button key={filter.key} variant="secondary" onClick={filter.onClear}>
              {filter.label}
              <X />
            </Button>
          ))}
          {onClearAll && applied.length > 1 && (
            <Button variant="link" onClick={onClearAll}>Clear all</Button>
          )}
        </div>
      )}
    </div>
  );
}
