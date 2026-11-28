import React, { Fragment, useState } from 'react';
import { Link } from 'react-router-dom';
import { History, Trash2 } from 'lucide-react';
import { ConfirmImpactDialog } from '../ConfirmImpactDialog.tsx';
import { Badge } from '../ui/badge.tsx';
import { Button } from '../ui/button.tsx';
import { EmptyState } from '../ui/empty-state.tsx';
import { Skeleton, TableSkeletonRows } from '../ui/skeleton.tsx';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '../ui/table.tsx';
import { RowToggle, RowToggleHead } from '../ui/row-toggle.tsx';
import { useFittedColumnWidths } from '../../lib/useColumnWidths.tsx';
import { formatTimestamp } from '../../lib/utils.ts';
import {
  RESULT_LABELS, fetchSyncRunItems, runSummary,
  type DriverSyncResult, type DriverSyncRun, type DriverSyncRunItem,
} from '../../lib/drivers.ts';
import { DriverCard } from './DriverCard.tsx';
import { SyncOutcome } from './SyncOutcome.tsx';

const RESULT_VARIANT: Record<DriverSyncResult, 'info' | 'success' | 'warning' | 'negative'> = {
  Running: 'info',
  Completed: 'success',
  PartlyFailed: 'warning',
  Failed: 'negative',
};

const ITEM_COLUMNS = ['model', 'component', 'previous', 'current', 'outcome'] as const;
const FITTED_COLUMNS = ['previous', 'current', 'outcome'] as const;

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
  return <RunItemsTable items={items} />;
}

/**
 * One line per item. Previous, Current and Outcome start as wide as their longest value in the
 * run, Model and Component share the rest and wrap to two lines, and every edge can be dragged.
 */
function RunItemsTable({ items }: { items: DriverSyncRunItem[] }): React.ReactElement {
  const { tableRef, fixed, style, handle } = useFittedColumnWidths(ITEM_COLUMNS, FITTED_COLUMNS);
  return (
    <div className="px-4 py-3">
      <div className="overflow-hidden rounded-md border border-border bg-background">
        <Table ref={tableRef} className={fixed ? 'table-fixed' : 'table-auto'}>
          <TableHeader>
            <TableRow>
              <TableHead className="relative" style={style('model')}>Model{handle('model', 'Model')}</TableHead>
              <TableHead className="relative" style={style('component')}>Component{handle('component', 'Component')}</TableHead>
              <TableHead className="relative" style={style('previous')}>Previous{handle('previous', 'Previous')}</TableHead>
              <TableHead className="relative" style={style('current')}>Current{handle('current', 'Current')}</TableHead>
              <TableHead style={style('outcome')}>Outcome</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {items.map((item, i) => {
              const component = item.componentName ?? item.lineageKey;
              return (
                <TableRow key={i}>
                  <TableCell>
                    {item.modelName
                      ? <span className="line-clamp-2" title={item.modelName}>{item.modelName}</span>
                      : item.componentName ? <span className="text-muted-foreground">Every model that uses it</span> : null}
                  </TableCell>
                  <TableCell>
                    {item.modelId && item.lineageKey ? (
                      <Link className="line-clamp-2 hover:underline" title={component ?? undefined} to={`/drivers/models/${item.modelId}${item.release ? `?release=${item.release}` : ''}#${item.lineageKey}`}>{component}</Link>
                    ) : (
                      <span className="line-clamp-2" title={item.componentName ?? item.reason ?? undefined}>{item.componentName ?? item.reason}</span>
                    )}
                  </TableCell>
                  <TableCell className="truncate whitespace-nowrap tabular-nums text-muted-foreground" title={item.oldVersion ?? undefined}>{item.oldVersion}</TableCell>
                  <TableCell className="truncate whitespace-nowrap tabular-nums" title={item.newVersion ?? undefined}>{item.newVersion}</TableCell>
                  <TableCell className={item.reason ? undefined : 'whitespace-nowrap'}>
                    {/* A reason wraps, so the layout the widths are measured from gives it 150px of text. */}
                    <div className={item.reason && !fixed ? 'w-[174px]' : undefined}>
                      <SyncOutcome outcome={item.outcome} reason={item.reason} inlineReason />
                    </div>
                  </TableCell>
                </TableRow>
              );
            })}
          </TableBody>
        </Table>
      </div>
    </div>
  );
}

/** Rows shown at first, and added by each Load more. */
export const HISTORY_PAGE = 20;

/** The History card: one row per run of the last 2 months, expandable to its items. */
export function SyncHistory({ runs, onClear }: {
  runs: DriverSyncRun[] | null;
  /** Set for Administrators: clears every finished run; true once the list is updated, false when it failed and was reported. */
  onClear?: () => Promise<boolean>;
}): React.ReactElement {
  const [open, setOpen] = useState<string | null>(null);
  const [shown, setShown] = useState(HISTORY_PAGE);
  const [confirming, setConfirming] = useState(false);
  const [clearing, setClearing] = useState(false);
  const hasItems = (run: DriverSyncRun) => Object.values(run.counts).some(n => (n ?? 0) > 0);
  const visible = runs?.slice(0, shown) ?? null;
  // Without a run to expand, an empty chevron column would only push every column to the right.
  const expandable = visible?.some(hasItems) ?? false;
  const columns = expandable ? 4 : 3;
  const finished = runs?.filter(r => r.result !== 'Running').length ?? 0;

  const clear = async () => {
    if (!onClear) return;
    setClearing(true);
    const cleared = await onClear();
    setClearing(false);
    if (cleared) {
      setConfirming(false);
      setShown(HISTORY_PAGE);
    }
  };

  return (
    <DriverCard
      title="History"
      description="Every sync and check for updates in the last 2 months."
      aside={onClear && (
        <Button variant="outline" onClick={() => setConfirming(true)} disabled={finished === 0}><Trash2 />Clear history</Button>
      )}
    >
      <div className="px-5 py-4">
        <div className="overflow-hidden rounded-md border border-border">
          <Table className="table-fixed">
            <TableHeader>
              <TableRow>
                {expandable && <RowToggleHead />}
                <TableHead className="w-[168px]">Started</TableHead>
                <TableHead className="w-[150px]">Result</TableHead>
                <TableHead>Details</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {visible === null ? (
                <TableSkeletonRows columns={3} rows={3} />
              ) : visible.length === 0 ? (
                <TableRow className="hover:bg-transparent">
                  <TableCell colSpan={columns} className="p-0">
                    <EmptyState icon={History} title="No syncs yet" description="Runs appear here when the schedule, Sync now or Check for updates starts one." />
                  </TableCell>
                </TableRow>
              ) : visible.map(run => {
                const expanded = open === run.runId;
                const summary = runSummary(run);
                return (
                  <Fragment key={run.runId}>
                    <TableRow>
                      {expandable && (
                        <TableCell className="pr-0">
                          {hasItems(run) && (
                            <RowToggle
                              expanded={expanded}
                              label={`${expanded ? 'Hide' : 'Show'} the changes of the run started ${formatTimestamp(run.startedAt)}`}
                              onToggle={() => setOpen(expanded ? null : run.runId)}
                            />
                          )}
                        </TableCell>
                      )}
                      <TableCell>{formatTimestamp(run.startedAt)}</TableCell>
                      <TableCell><Badge variant={RESULT_VARIANT[run.result]}>{RESULT_LABELS[run.result]}</Badge></TableCell>
                      <TableCell className="truncate" title={summary}>{summary}</TableCell>
                    </TableRow>
                    {expanded && (
                      <TableRow>
                        <TableCell colSpan={columns} className="bg-muted/30 p-0"><RunItems runId={run.runId} /></TableCell>
                      </TableRow>
                    )}
                  </Fragment>
                );
              })}
            </TableBody>
          </Table>
        </div>
        {runs && runs.length > shown && (
          <div className="mt-3 flex items-center justify-between gap-4">
            <p className="text-sm text-muted-foreground">{`Showing ${shown} of ${runs.length}`}</p>
            <Button variant="outline" onClick={() => setShown(n => n + HISTORY_PAGE)}>Load more</Button>
          </div>
        )}
      </div>
      {confirming && (
        <ConfirmImpactDialog
          icon={Trash2}
          titleId="clear-sync-history-title"
          copy={{
            confirmTitle: 'Clear the sync history?',
            impact: `Removes ${finished === 1 ? 'the finished sync' : `all ${finished} finished syncs and checks for updates`}, with their changes, from this list and from the Driver Synchronization report. A sync that is running stays. This cannot be undone.`,
            confirmLabel: 'Clear history',
            destructive: true,
          }}
          busy={clearing}
          onCancel={() => setConfirming(false)}
          onConfirm={() => void clear()}
        />
      )}
    </DriverCard>
  );
}
