import * as React from 'react';
import { cn } from '../../lib/utils';
import { TableCell, TableRow } from './table';

function Skeleton({ className, ...props }: React.HTMLAttributes<HTMLDivElement>): React.ReactElement {
  return <div className={cn('animate-pulse rounded-md bg-muted', className)} {...props} />;
}

interface TableSkeletonRowsProps {
  /** Number of columns in the table. Must match the header, or the rows will not line up. */
  columns: number;
  /** Placeholder row count. Enough to fill the fold without implying a specific result count. */
  rows?: number;
}

/**
 * Placeholder rows for a table whose first load is still in flight.
 *
 * Rows rather than a centred "Loading..." string, and rather than nothing at all: a table that
 * renders an empty body collapses to its header, so the page visibly jumps when the data lands,
 * and on a slow first call it is indistinguishable from a table that legitimately has no rows.
 * Occupying the space keeps the layout stable and tells the user which of the two they are
 * looking at.
 */
function TableSkeletonRows({ columns, rows = 5 }: TableSkeletonRowsProps): React.ReactElement {
  return (
    <>
      {Array.from({ length: rows }).map((_, row) => (
        <TableRow key={`skeleton-${row}`} className="hover:bg-transparent">
          {Array.from({ length: columns }).map((__, column) => (
            <TableCell key={column}>
              <Skeleton className="h-4 w-full max-w-[8rem]" />
            </TableCell>
          ))}
        </TableRow>
      ))}
    </>
  );
}

export { Skeleton, TableSkeletonRows };
