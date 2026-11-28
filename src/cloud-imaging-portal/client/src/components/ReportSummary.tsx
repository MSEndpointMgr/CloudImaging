import * as React from 'react';
import { cn } from '../lib/utils.ts';
import { share } from '../lib/reportSummary.ts';
import { Card, CardContent } from './ui/card.tsx';
import { Skeleton } from './ui/skeleton.tsx';

/** One part of the breakdown: a bar segment and, unless a figure shows it, a legend entry. */
export interface SummaryPart {
  key: string;
  label: string;
  count: number;
  /** Background classes of the segment and the legend dot, from `SUMMARY_COLORS`. */
  colorClassName: string;
  /** Muted text after the count. Defaults to the part's share of the total while it has one. */
  detail?: string;
  /** False keeps the part in the bar but out of the legend, for a part one of the figures shows. */
  inLegend?: boolean;
}

/** One key figure beside the breakdown. */
export interface SummaryFigure {
  label: string;
  /** Null reads "None", muted. */
  value: string | null;
  detail?: string;
}

interface ReportSummaryProps {
  loading: boolean;
  total: number;
  /** The noun after the total, singular then plural. */
  noun: readonly [string, string];
  /** Note at the end of the total's line, such as "30 scheduled, 4 started by hand". */
  aside?: string;
  parts: readonly SummaryPart[];
  /** Replaces the legend while the total is 0. */
  emptyText: string;
  /** Two figures, or four. */
  figures: readonly SummaryFigure[];
}

// Beside the figures once the window leaves the legend room for one line (four figures need a
// wider one), with the breakdown never narrower than its legend. Below that the breakdown takes
// the row and the figures sit under it.
const LAYOUTS = {
  two: {
    grid: 'sm:grid-cols-2 xl:grid-cols-[minmax(max-content,2.6fr)_repeat(2,minmax(0,1fr))]',
    breakdown: 'sm:col-span-2 xl:col-span-1',
  },
  four: {
    grid: 'sm:grid-cols-2 lg:grid-cols-4 2xl:grid-cols-[minmax(max-content,2.6fr)_repeat(4,minmax(0,1fr))]',
    breakdown: 'sm:col-span-2 lg:col-span-4 2xl:col-span-1',
  },
} as const;

/**
 * The summary above a report's table: one card breaking the total down with a bar and a one-line
 * legend, and the key figures beside it. It replaced a grid of one card per number, which wrapped
 * seven cards into rows of four and three and showed nothing of how the outcomes relate.
 */
export function ReportSummary({ loading, total, noun, aside, parts, emptyText, figures }: ReportSummaryProps): React.ReactElement {
  const layout = figures.length > 2 ? LAYOUTS.four : LAYOUTS.two;

  return (
    <div className={cn('grid gap-4', layout.grid)}>
      <Card className={cn('px-5 py-4', layout.breakdown)}>
        <div className="flex items-baseline gap-2">
          {loading
            ? <Skeleton className="h-8 w-12 self-center" />
            : <span className="text-2xl font-semibold tabular-nums">{total}</span>}
          <span className="text-sm text-muted-foreground">{total === 1 ? noun[0] : noun[1]}</span>
          {aside && !loading && <span className="ml-auto text-xs text-muted-foreground">{aside}</span>}
        </div>
        {loading ? (
          <>
            <Skeleton className="my-3 h-2 w-full rounded-full" />
            <Skeleton className="h-5 w-2/3" />
          </>
        ) : (
          <>
            {/* The legend carries every number, so the bar is only its picture. */}
            <div aria-hidden="true" className={cn('my-3 flex h-2 gap-0.5 overflow-hidden rounded-full', total === 0 && 'bg-muted')}>
              {parts.filter(part => part.count > 0).map(part => (
                <span key={part.key} className={cn('min-w-1 basis-0', part.colorClassName)} style={{ flexGrow: part.count }} />
              ))}
            </div>
            {total === 0 ? (
              <p className="text-sm text-muted-foreground">{emptyText}</p>
            ) : (
              <ul className="flex flex-wrap gap-x-5 gap-y-2 text-sm">
                {parts.filter(part => part.inLegend !== false).map(part => {
                  const detail = part.detail ?? (part.count > 0 ? `${share(part.count, total)}%` : undefined);
                  return (
                    <li key={part.key} className="inline-flex items-center gap-2 whitespace-nowrap">
                      <span aria-hidden="true" className={cn('h-2 w-2 shrink-0 rounded-full', part.colorClassName)} />
                      {part.label}
                      <span className="font-semibold tabular-nums">{part.count}</span>
                      {detail && <span className="text-muted-foreground">{detail}</span>}
                    </li>
                  );
                })}
              </ul>
            )}
          </>
        )}
      </Card>
      {figures.map(figure => (
        <Card key={figure.label}>
          <CardContent className="p-4">
            <p className="text-xs text-muted-foreground">{figure.label}</p>
            {loading ? (
              <Skeleton className="mt-1 h-8 w-16" />
            ) : (
              <p className={cn('mt-1 text-2xl font-semibold tabular-nums', figure.value === null && 'text-muted-foreground')}>
                {figure.value ?? 'None'}
              </p>
            )}
            {!loading && figure.detail && <p className="mt-1 text-balance text-xs text-muted-foreground">{figure.detail}</p>}
          </CardContent>
        </Card>
      ))}
    </div>
  );
}
