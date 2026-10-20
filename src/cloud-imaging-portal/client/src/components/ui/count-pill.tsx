import { cn } from '../../lib/utils.ts';

interface CountPillProps {
  count: number;
  /** When set, renders "count of total", e.g. while a search narrows a table. */
  total?: number;
  className?: string;
}

/** Item count shown next to a table's title, matching the Devices page's section counts. */
export function CountPill({ count, total, className }: CountPillProps): React.ReactElement {
  return (
    <span
      className={cn(
        'inline-flex min-w-5 select-none cursor-default items-center justify-center rounded-full bg-muted px-2 py-0.5 text-xs font-semibold tabular-nums text-muted-foreground',
        className,
      )}
    >
      {total !== undefined && total !== count ? `${String(count)} of ${String(total)}` : String(count)}
    </span>
  );
}
