import React, { Fragment, useState } from 'react';
import { Link } from 'react-router-dom';
import { ChevronDown, ChevronRight, History } from 'lucide-react';
import { Badge } from '../ui/badge.tsx';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '../ui/card.tsx';
import { EmptyState } from '../ui/empty-state.tsx';
import { Skeleton } from '../ui/skeleton.tsx';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '../ui/table.tsx';
import { formatTimestamp } from '../../lib/utils.ts';
import {
  RESULT_LABELS, fetchSyncRunItems, outcomeLabel, runSummary,
  type DriverSyncResult, type DriverSyncRun, type DriverSyncRunItem,
} from '../../lib/drivers.ts';

const RESULT_VARIANT: Record<DriverSyncResult, 'info' | 'success' | 'warning' | 'negative'> = {
  Running: 'info',
  Completed: 'success',
  PartlyFailed: 'warning',
  Failed: 'negative',
};

function change(item: DriverSyncRunItem): string {
  if (item.oldVersion && item.newVersion) return `${item.oldVersion} to ${item.newVersion}`;
  return item.newVersion ?? item.oldVersion ?? '';
}

/** Items of one run, loaded when it is expanded. */
function RunItems({ runId }: { runId: string }): React.ReactElement {
  const [items, setItems] = useState<DriverSyncRunItem[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  React.useEffect(() => {
    let cancelled = false;
    fetchSyncRunItems(runId)
      .then(list => { if (!cancelled) setItems(list); })
      .catch((err: unknown) => { if (!cancelled) setError(err instanceof Error ? err.message : 'Could not load the run.'); });
    return () => { cancelled = true; };
  }, [runId]);

  if (error) return <p className="px-4 py-3 text-sm text-destructive">{error}</p>;
  if (!items) return <div className="px-4 py-3"><Skeleton className="h-6 w-full" /></div>;
  return (
    <Table className="table-fixed">
      <TableHeader>
        <TableRow>
          <TableHead className="w-[28%]">Model</TableHead>
          <TableHead>Component</TableHead>
          <TableHead className="w-[20%]">Change</TableHead>
          <TableHead className="w-[22%]">Outcome</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {items.map((item, i) => (
          <TableRow key={i}>
            <TableCell className="truncate" title={item.modelName ?? undefined}>
              {item.modelName ?? (item.componentName ? <span className="text-muted-foreground">Every model that uses it</span> : null)}
            </TableCell>
            <TableCell className="truncate">
              {item.modelId && item.lineageKey ? (
                <Link className="hover:underline" to={`/drivers/models/${item.modelId}${item.release ? `?release=${item.release}` : ''}#${item.lineageKey}`}>{item.componentName ?? item.lineageKey}</Link>
              ) : (item.componentName ?? item.reason)}
            </TableCell>
            <TableCell className="truncate">{change(item)}</TableCell>
            <TableCell className="truncate" title={item.reason ?? undefined}>{item.reason ? `${outcomeLabel(item.outcome)}: ${item.reason}` : outcomeLabel(item.outcome)}</TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}

/** The History card: one row per run of the last 90 days, expandable to its items. */
export function SyncHistory({ runs }: { runs: DriverSyncRun[] | null }): React.ReactElement {
  const [open, setOpen] = useState<string | null>(null);
  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-base">History</CardTitle>
        <CardDescription>Every sync and check for updates in the last 90 days.</CardDescription>
      </CardHeader>
      <CardContent>
        {runs === null ? (
          <Skeleton className="h-24 w-full" />
        ) : runs.length === 0 ? (
          <EmptyState icon={History} title="No syncs yet" description="Runs appear here when the schedule, Sync now or Check for updates starts one." />
        ) : (
          <Table className="table-fixed">
            <TableHeader>
              <TableRow>
                <TableHead className="w-[44px]"><span className="sr-only">Details</span></TableHead>
                <TableHead className="w-[168px]">Started</TableHead>
                <TableHead className="w-[150px]">Result</TableHead>
                <TableHead>Details</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {runs.map(run => {
                const hasItems = Object.values(run.counts).some(n => (n ?? 0) > 0);
                const expanded = open === run.runId;
                const summary = runSummary(run);
                return (
                  <Fragment key={run.runId}>
                    <TableRow>
                      <TableCell>
                        {hasItems && (
                          <button
                            type="button"
                            aria-expanded={expanded}
                            aria-label={`${expanded ? 'Hide' : 'Show'} the changes of the run started ${formatTimestamp(run.startedAt)}`}
                            className="inline-flex rounded-md p-1 text-muted-foreground hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
                            onClick={() => setOpen(expanded ? null : run.runId)}
                          >
                            {expanded ? <ChevronDown className="h-4 w-4" aria-hidden="true" /> : <ChevronRight className="h-4 w-4" aria-hidden="true" />}
                          </button>
                        )}
                      </TableCell>
                      <TableCell>{formatTimestamp(run.startedAt)}</TableCell>
                      <TableCell><Badge variant={RESULT_VARIANT[run.result]}>{RESULT_LABELS[run.result]}</Badge></TableCell>
                      <TableCell className="truncate" title={summary}>{summary}</TableCell>
                    </TableRow>
                    {expanded && (
                      <TableRow>
                        <TableCell colSpan={4} className="bg-muted/30 p-0"><RunItems runId={run.runId} /></TableCell>
                      </TableRow>
                    )}
                  </Fragment>
                );
              })}
            </TableBody>
          </Table>
        )}
      </CardContent>
    </Card>
  );
}
