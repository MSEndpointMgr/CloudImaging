import React, { Fragment, useCallback, useEffect, useMemo, useState } from 'react';
import { AlertTriangle, ChevronDown, ChevronRight, CircleCheck, CircleDashed, CircleX, FileDown, RefreshCw } from 'lucide-react';
import { Bar, BarChart, CartesianGrid, ResponsiveContainer, Tooltip as ChartTooltip, XAxis, YAxis } from 'recharts';
import { Badge } from '../components/ui/badge.tsx';
import { Button } from '../components/ui/button.tsx';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '../components/ui/card.tsx';
import { DateRangeSelect } from '../components/ui/date-range-select.tsx';
import { EmptyState } from '../components/ui/empty-state.tsx';
import { SearchInput } from '../components/ui/search-input.tsx';
import { Select } from '../components/ui/select.tsx';
import { Skeleton, TableSkeletonRows } from '../components/ui/skeleton.tsx';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '../components/ui/table.tsx';
import { Tooltip } from '../components/ui/tooltip.tsx';
import { ReportFilterBar, type AppliedFilter } from '../components/ReportFilterBar.tsx';
import { SyncOutcome } from '../components/drivers/SyncOutcome.tsx';
import { useDebouncedValue } from '../hooks/useDebouncedValue.ts';
import { downloadBlob, toCsv } from '../lib/csv.ts';
import { resolvePreset, type DateRange } from '../lib/dateRange.ts';
import { formatTimestamp } from '../lib/utils.ts';
import {
  RESULT_LABELS, TRIGGER_LABELS, countsSummary, durationLabel, fetchSyncReport, formatSize, outcomeLabel, releaseLabel, vendorLabel,
  type DriverSyncReportRun, type DriverSyncResult, type DriverSyncRunItem, type DriverVendor,
} from '../lib/drivers.ts';
import {
  changesPerWeek, filterSyncReport, groupItems, itemCounts, recurringFailures, syncReportTotals, unreadCatalogs, type SyncTriggerFilter,
} from '../lib/driverSyncReport.ts';

const VENDOR_FILTERS: { value: '' | DriverVendor; label: string }[] = [
  { value: '', label: 'All vendors' },
  { value: 'Hp', label: 'HP' },
  { value: 'Dell', label: 'Dell' },
];

const TRIGGER_FILTERS: { value: SyncTriggerFilter; label: string }[] = [
  { value: '', label: 'All triggers' },
  { value: 'Scheduled', label: 'Schedule' },
  { value: 'Manual', label: 'Sync now' },
  { value: 'Check', label: 'Check for updates' },
];

const RESULT_VARIANT: Record<DriverSyncResult, 'info' | 'success' | 'warning' | 'negative'> = {
  Running: 'info',
  Completed: 'success',
  PartlyFailed: 'warning',
  Failed: 'negative',
};

const SERIES = [
  { key: 'updated', label: 'Updated', className: 'text-blue-500', swatch: 'bg-blue-500' },
  { key: 'fresh', label: 'New', className: 'text-emerald-500', swatch: 'bg-emerald-500' },
  { key: 'withdrawn', label: 'Withdrawn', className: 'text-slate-500', swatch: 'bg-slate-500' },
  { key: 'failed', label: 'Failed', className: 'text-red-500', swatch: 'bg-red-500' },
] as const;

function plural(count: number, one: string, many: string): string {
  return `${count} ${count === 1 ? one : many}`;
}

function version(item: DriverSyncRunItem): string {
  if (item.oldVersion && item.newVersion) return `${item.oldVersion} to ${item.newVersion}`;
  return item.newVersion ?? item.oldVersion ?? '';
}

/** The Changes cell: what the run changed, or why a run without changes failed. */
function changesText({ run, items }: DriverSyncReportRun): string {
  if (run.result === 'Running') return 'Running';
  const runFailure = items.find(i => !i.modelId && !i.componentName && i.reason);
  const failedCatalogs = run.vendors.filter(v => !v.processed).map(v => vendorLabel(v.vendor));
  const unread = unreadCatalogs(run).map(vendorLabel);
  let text = countsSummary(itemCounts(items.filter(i => i.modelId || i.componentName)));
  if (!text) {
    text = runFailure?.reason
      ?? (failedCatalogs.length > 0 ? `${failedCatalogs.join(' and ')} catalog failed`
        : unread.length > 0 ? `${unread.join(' and ')} catalog not read` : 'No changes');
  }
  return (run.trigger === 'ModelCheck' || run.trigger === 'Onboarding') && run.modelName ? `${run.modelName}: ${text}` : text;
}

function Field({ label, children }: { label: string; children: React.ReactNode }): React.ReactElement {
  return (
    <div className="min-w-0">
      <dt className="text-xs text-muted-foreground">{label}</dt>
      <dd className="flex min-w-0 items-center gap-2 truncate text-sm">{children}</dd>
    </div>
  );
}

/** One synchronization opened: labelled fields, then its changes grouped by model and Windows release. */
function RunDetails({ entry }: { entry: DriverSyncReportRun }): React.ReactElement {
  const { run, items } = entry;
  const groups = groupItems(items);
  const runFailures = items.filter(i => !i.modelId && !i.componentName && i.reason);
  return (
    <div className="space-y-4 py-4 pl-14 pr-5">
      <p className="text-xs font-semibold uppercase tracking-wide text-muted-foreground">Synchronization</p>
      <dl className="grid gap-x-8 gap-y-3 sm:grid-cols-2 lg:grid-cols-4">
        {run.vendors.map(v => (
          <Field key={v.vendor} label={`${vendorLabel(v.vendor)} catalog`}>
            {v.processed ? (
              <>
                <CircleCheck className="h-4 w-4 shrink-0 text-emerald-600 dark:text-emerald-400" aria-hidden="true" />
                <span className="truncate">{v.newModels > 0 ? `Read, ${plural(v.newModels, 'new model', 'new models')}` : 'Read'}</span>
              </>
            ) : (
              <>
                <Tooltip content={v.failureReason ?? 'The catalog could not be read.'}>
                  <span tabIndex={0} aria-label={`Failed: ${v.failureReason ?? 'The catalog could not be read.'}`} className="inline-flex shrink-0">
                    <CircleX className="h-4 w-4 text-destructive" aria-hidden="true" />
                  </span>
                </Tooltip>
                <span className="truncate">Failed</span>
              </>
            )}
          </Field>
        ))}
        {unreadCatalogs(run).map(vendor => (
          <Field key={vendor} label={`${vendorLabel(vendor)} catalog`}>
            <CircleDashed className="h-4 w-4 shrink-0 text-muted-foreground" aria-hidden="true" />
            <span className="truncate">{run.result === 'Running' ? 'Not read yet' : 'Not read'}</span>
          </Field>
        ))}
        <Field label="Downloaded">
          {run.downloadedComponents === undefined ? 'Not recorded' : `${plural(run.downloadedComponents, 'component', 'components')}, ${formatSize(run.downloadedBytes ?? 0)}`}
        </Field>
        <Field label="Finished">{run.finishedAt ? formatTimestamp(run.finishedAt) : 'Running'}</Field>
        <Field label="Models checked">{run.modelsTotal === undefined ? 'Not recorded' : `${run.modelsChecked ?? 0} of ${run.modelsTotal}`}</Field>
        <Field label="New driver sets">{run.newSets === undefined ? 'Not recorded' : String(run.newSets)}</Field>
      </dl>
      {runFailures.map((failure, i) => (
        <p key={i} className="flex items-center gap-2 text-sm">
          <CircleX className="h-4 w-4 shrink-0 text-destructive" aria-hidden="true" />
          {failure.reason}
        </p>
      ))}
      {groups.length > 0 && (
        <>
          <p className="text-xs font-semibold uppercase tracking-wide text-muted-foreground">Changes by model</p>
          <div className="space-y-3">
            {groups.map(group => (
              <div key={group.key} className="overflow-hidden rounded-lg border border-border bg-card">
                <div className="flex items-center gap-3 border-b border-border px-3 py-2 text-sm">
                  <span className="font-semibold">{group.modelName ?? 'Downloads'}</span>
                  <span className="text-muted-foreground">
                    {group.release ? releaseLabel(group.release) : group.vendor ? `${vendorLabel(group.vendor)} components used by several models` : ''}
                  </span>
                  <span className="ml-auto text-muted-foreground">{countsSummary(itemCounts(group.items))}</span>
                </div>
                <Table className="table-fixed">
                  <TableHeader>
                    <TableRow>
                      <TableHead>Component</TableHead>
                      <TableHead className="w-[18%]">Category</TableHead>
                      <TableHead className="w-[30%]">Version</TableHead>
                      <TableHead className="w-[16%]">Outcome</TableHead>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {group.items.map((item, i) => (
                      <TableRow key={i}>
                        <TableCell className="truncate" title={item.componentName ?? item.reason ?? undefined}>
                          {item.componentName ?? <span className="text-muted-foreground">{item.reason}</span>}
                        </TableCell>
                        <TableCell className="truncate text-muted-foreground" title={item.category ?? undefined}>{item.category}</TableCell>
                        <TableCell className="truncate" title={version(item)}>{version(item)}</TableCell>
                        <TableCell><SyncOutcome outcome={item.outcome} reason={item.outcome === 'Failed' && item.componentName ? item.reason : null} /></TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              </div>
            ))}
          </div>
        </>
      )}
    </div>
  );
}

/**
 * Driver Synchronization report: every sync and check for updates of a period, how reliable and
 * long they were, what they changed and downloaded, and which components keep failing. Runs are
 * kept for 90 days.
 */
export default function ReportDriverSyncPage(): React.ReactElement {
  const [range, setRange] = useState<DateRange>(() => resolvePreset('last30')!);
  const [report, setReport] = useState<DriverSyncReportRun[] | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [attempt, setAttempt] = useState(0);
  const [search, setSearch] = useState('');
  const debouncedSearch = useDebouncedValue(search);
  const [vendor, setVendor] = useState<'' | DriverVendor>('');
  const [trigger, setTrigger] = useState<SyncTriggerFilter>('');
  const [open, setOpen] = useState<string | null>(null);
  const { from, to } = range;

  useEffect(() => {
    let cancelled = false;
    setReport(null);
    setLoadError(null);
    fetchSyncReport(`${from}T00:00:00.000Z`, `${to}T23:59:59.999Z`)
      .then(loaded => { if (!cancelled) setReport(loaded); })
      .catch((err: unknown) => { if (!cancelled) setLoadError(err instanceof Error ? err.message : 'Could not load the synchronizations.'); });
    return () => { cancelled = true; };
  }, [from, to, attempt]);

  const retry = useCallback(() => setAttempt(n => n + 1), []);
  const filtered = useMemo(() => filterSyncReport(report ?? [], { search: debouncedSearch, vendor, trigger }), [report, debouncedSearch, vendor, trigger]);
  const totals = useMemo(() => syncReportTotals(filtered), [filtered]);
  const weeks = useMemo(() => changesPerWeek(filtered, from, to), [filtered, from, to]);
  const failures = useMemo(() => recurringFailures(filtered), [filtered]);

  const applied = useMemo<AppliedFilter[]>(() => {
    const chips: AppliedFilter[] = [];
    if (search) chips.push({ key: 'search', label: `Search: ${search}`, onClear: () => setSearch('') });
    if (vendor) chips.push({ key: 'vendor', label: `Vendor: ${vendorLabel(vendor)}`, onClear: () => setVendor('') });
    if (trigger) chips.push({ key: 'trigger', label: `Trigger: ${TRIGGER_FILTERS.find(t => t.value === trigger)?.label ?? trigger}`, onClear: () => setTrigger('') });
    return chips;
  }, [search, vendor, trigger]);

  const exportCsv = (): void => {
    const rows = filtered.flatMap(({ run, items }) => items.map(item => ({ run, item })));
    downloadBlob(`driver-synchronizations-${from}-to-${to}.csv`, toCsv(rows, [
      { header: 'Started', accessor: r => r.run.startedAt },
      { header: 'Trigger', accessor: r => TRIGGER_LABELS[r.run.trigger] },
      { header: 'Started By', accessor: r => r.run.startedBy },
      { header: 'Result', accessor: r => RESULT_LABELS[r.run.result] },
      { header: 'Vendor', accessor: r => (r.item.vendor ? vendorLabel(r.item.vendor) : null) },
      { header: 'Model', accessor: r => r.item.modelName },
      { header: 'Windows Release', accessor: r => (r.item.release ? releaseLabel(r.item.release) : null) },
      { header: 'Component', accessor: r => r.item.componentName },
      { header: 'Category', accessor: r => r.item.category },
      { header: 'Old Version', accessor: r => r.item.oldVersion },
      { header: 'New Version', accessor: r => r.item.newVersion },
      { header: 'Outcome', accessor: r => outcomeLabel(r.item.outcome) },
      { header: 'Reason', accessor: r => r.item.reason },
      { header: 'Recorded At', accessor: r => r.item.at ?? null },
    ]));
  };

  const loading = report === null;
  const completedShare = totals.finished === 0 ? null : Math.round((totals.completed / totals.finished) * 100);
  const failureParts = [
    totals.failedComponents > 0 ? plural(totals.failedComponents, 'component', 'components') : null,
    totals.catalogFailures > 0 ? plural(totals.catalogFailures, 'vendor catalog', 'vendor catalogs') : null,
    totals.otherFailures > 0 ? plural(totals.otherFailures, 'synchronization', 'synchronizations') : null,
  ].filter(Boolean);
  const tiles = [
    { label: 'Synchronizations', value: String(totals.runs), sub: `${totals.scheduled} scheduled, ${totals.byHand} started by hand` },
    { label: 'Completed', value: completedShare === null ? 'None' : `${completedShare}%`, sub: `${totals.partlyFailed} partly failed, ${totals.failed} failed` },
    { label: 'Average duration', value: totals.averageMs === null ? 'None' : durationLabel(totals.averageMs), sub: totals.longestMs === null ? 'No finished synchronizations' : `Longest ${durationLabel(totals.longestMs)}` },
    { label: 'Components changed', value: String(totals.changed), sub: `${totals.updated} updated, ${totals.fresh} new, ${totals.withdrawn} withdrawn` },
    { label: 'Downloaded', value: formatSize(totals.downloadedBytes), sub: plural(totals.downloadedComponents, 'component', 'components') },
    { label: 'Failures', value: String(totals.componentFailures + totals.catalogFailures + totals.otherFailures), sub: failureParts.length > 0 ? failureParts.join(', ') : 'None' },
  ];

  return (
    <div className="space-y-6">
      <div className="max-w-3xl space-y-1">
        <div className="flex items-center gap-2">
          <RefreshCw size={24} className="text-primary" aria-hidden="true" />
          <h2 className="text-lg font-semibold">Driver synchronization</h2>
        </div>
        <p className="text-sm text-muted-foreground">Every synchronization and check for updates, with what it changed and downloaded. They are kept for 90 days.</p>
      </div>

      <ReportFilterBar
        applied={applied}
        onClearAll={() => { setSearch(''); setVendor(''); setTrigger(''); }}
        actions={<Button variant="outline" onClick={exportCsv} disabled={!filtered.some(entry => entry.items.length > 0)}><FileDown />Export CSV</Button>}
      >
        <SearchInput id="driver-sync-report-search" value={search} onValueChange={setSearch} placeholder="Model or component" wrapperClassName="w-80" />
        <DateRangeSelect id="driver-sync-report-range" aria-label="Started between" value={range} onValueChange={setRange} wrapperClassName="w-44" />
        <Select id="driver-sync-report-vendor" aria-label="Vendor" value={vendor} onValueChange={v => setVendor(v as '' | DriverVendor)} options={VENDOR_FILTERS} wrapperClassName="w-40" />
        <Select id="driver-sync-report-trigger" aria-label="Trigger" value={trigger} onValueChange={v => setTrigger(v as SyncTriggerFilter)} options={TRIGGER_FILTERS} wrapperClassName="w-44" />
      </ReportFilterBar>

      {loadError ? (
        <div role="alert" className="flex items-start gap-3 rounded-md border border-destructive/40 bg-destructive/5 p-4">
          <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0 text-destructive" aria-hidden="true" />
          <div className="flex-1 space-y-1">
            <p className="text-sm font-medium">Could not load the synchronizations</p>
            <p className="text-sm text-muted-foreground">{loadError}</p>
          </div>
          <Button variant="outline" onClick={retry}><RefreshCw />Try again</Button>
        </div>
      ) : (
        <>
          <div className="grid gap-4 sm:grid-cols-3 lg:grid-cols-6">
            {tiles.map(tile => (
              <Card key={tile.label}>
                <CardContent className="p-4">
                  <p className="text-xs text-muted-foreground">{tile.label}</p>
                  <div className="mt-1 text-2xl font-semibold tabular-nums">{loading ? <Skeleton className="h-8 w-12" /> : tile.value}</div>
                  <p className="mt-1 truncate text-xs text-muted-foreground" title={tile.sub}>{loading ? '' : tile.sub}</p>
                </CardContent>
              </Card>
            ))}
          </div>

          <Card>
            <CardHeader className="flex-row items-center gap-4 space-y-0">
              <div className="flex-1 space-y-2">
                <CardTitle className="text-base">Changes per week</CardTitle>
                <CardDescription>Components the synchronizations changed, by outcome.</CardDescription>
              </div>
              <div className="flex flex-wrap gap-4 text-xs text-muted-foreground">
                {SERIES.map(s => <span key={s.key} className="inline-flex items-center gap-2"><span className={`h-3 w-3 rounded-sm ${s.swatch}`} aria-hidden="true" />{s.label}</span>)}
              </div>
            </CardHeader>
            <CardContent>
              {loading ? (
                <Skeleton className="h-48 w-full" />
              ) : (
                <div className="h-48 text-muted-foreground" role="img" aria-label={`Changes per week: ${weeks.map(w => `week of ${w.week}, ${w.updated} updated, ${w.fresh} new, ${w.withdrawn} withdrawn, ${w.failed} failed`).join('; ')}`}>
                  <ResponsiveContainer width="100%" height="100%">
                    <BarChart data={weeks} margin={{ top: 4, right: 4, bottom: 0, left: -16 }}>
                      <CartesianGrid vertical={false} stroke="hsl(var(--border))" />
                      <XAxis dataKey="week" tickLine={false} axisLine={false} tick={{ fill: 'currentColor', fontSize: 12 }} />
                      <YAxis allowDecimals={false} tickLine={false} axisLine={false} tick={{ fill: 'currentColor', fontSize: 12 }} />
                      <ChartTooltip
                        cursor={{ fill: 'hsl(var(--muted))', opacity: 0.4 }}
                        contentStyle={{ background: 'hsl(var(--popover))', border: '1px solid hsl(var(--border))', borderRadius: 8, fontSize: 12 }}
                        labelFormatter={label => (typeof label === 'string' ? `Week of ${label}` : '')}
                      />
                      {SERIES.map(s => <Bar key={s.key} dataKey={s.key} name={s.label} stackId="changes" fill="currentColor" className={s.className} isAnimationActive={false} />)}
                    </BarChart>
                  </ResponsiveContainer>
                </div>
              )}
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle className="text-base">Synchronizations</CardTitle>
              <CardDescription>One row per synchronization. Open a row for its vendors, downloads and changes.</CardDescription>
            </CardHeader>
            <CardContent>
              <div className="overflow-hidden rounded-md border border-border">
                <Table className="table-fixed">
                  <TableHeader>
                    <TableRow>
                      <TableHead className="w-[48px]"><span className="sr-only">Details</span></TableHead>
                      <TableHead className="w-[150px]">Started</TableHead>
                      <TableHead className="w-[170px]">Trigger</TableHead>
                      <TableHead className="w-[22%]">Started by</TableHead>
                      <TableHead className="w-[96px] text-right">Duration</TableHead>
                      <TableHead className="w-[140px]">Result</TableHead>
                      <TableHead>Changes</TableHead>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {loading && <TableSkeletonRows columns={7} rows={4} />}
                    {!loading && filtered.length === 0 && (
                      <TableRow className="hover:bg-transparent">
                        <TableCell colSpan={7} className="p-0">
                          <EmptyState
                            icon={RefreshCw}
                            title={(report ?? []).length === 0 ? 'No synchronizations in this period' : 'No synchronizations match these filters'}
                            description={(report ?? []).length === 0 ? 'Scheduled syncs, Sync now and Check for updates appear here once they start.' : undefined}
                          />
                        </TableCell>
                      </TableRow>
                    )}
                    {filtered.map(entry => {
                      const { run } = entry;
                      const expanded = open === run.runId;
                      const changes = changesText(entry);
                      return (
                        <Fragment key={run.runId}>
                          <TableRow>
                            <TableCell className="pr-0">
                              <button
                                type="button"
                                aria-expanded={expanded}
                                aria-label={`${expanded ? 'Hide' : 'Show'} the details of the synchronization started ${formatTimestamp(run.startedAt)}`}
                                className="inline-flex rounded-md p-1 text-muted-foreground hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
                                onClick={() => setOpen(expanded ? null : run.runId)}
                              >
                                {expanded ? <ChevronDown className="h-4 w-4" aria-hidden="true" /> : <ChevronRight className="h-4 w-4" aria-hidden="true" />}
                              </button>
                            </TableCell>
                            <TableCell>{formatTimestamp(run.startedAt)}</TableCell>
                            <TableCell className="truncate">{TRIGGER_LABELS[run.trigger]}</TableCell>
                            <TableCell className="truncate" title={run.startedBy ?? undefined}>
                              {run.trigger === 'Scheduled' || !run.startedBy ? <span className="text-muted-foreground">Schedule</span> : run.startedBy}
                            </TableCell>
                            <TableCell className="text-right tabular-nums">{run.finishedAt ? durationLabel(Date.parse(run.finishedAt) - Date.parse(run.startedAt)) : 'Running'}</TableCell>
                            <TableCell><Badge variant={RESULT_VARIANT[run.result]}>{RESULT_LABELS[run.result]}</Badge></TableCell>
                            <TableCell className="truncate" title={changes}>{changes}</TableCell>
                          </TableRow>
                          {expanded && (
                            <TableRow className="hover:bg-transparent">
                              <TableCell colSpan={7} className="bg-muted/30 p-0"><RunDetails entry={entry} /></TableCell>
                            </TableRow>
                          )}
                        </Fragment>
                      );
                    })}
                  </TableBody>
                </Table>
              </div>
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle className="text-base">Recurring failures</CardTitle>
              <CardDescription>Components that failed to download or verify in this period, most failures first. Vendor catalog failures show on their synchronization.</CardDescription>
            </CardHeader>
            <CardContent>
              <div className="overflow-hidden rounded-md border border-border">
                <Table className="table-fixed">
                  <TableHeader>
                    <TableRow>
                      <TableHead className="w-[24%]">Component</TableHead>
                      <TableHead className="w-[22%]">Model</TableHead>
                      <TableHead className="w-[96px] text-right">Failures</TableHead>
                      <TableHead>Last reason</TableHead>
                      <TableHead className="w-[150px]">Last failed</TableHead>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {loading && <TableSkeletonRows columns={5} rows={2} />}
                    {!loading && failures.length === 0 && (
                      <TableRow className="hover:bg-transparent">
                        <TableCell colSpan={5} className="p-0">
                          <EmptyState icon={CircleCheck} title="No failures in this period" />
                        </TableCell>
                      </TableRow>
                    )}
                    {failures.map(failure => (
                      <TableRow key={`${failure.componentName}-${failure.modelName ?? ''}`}>
                        <TableCell className="truncate" title={failure.componentName}>{failure.componentName}</TableCell>
                        <TableCell className="truncate" title={failure.modelName ?? undefined}>
                          {failure.modelName ?? <span className="text-muted-foreground">Every model that uses it</span>}
                        </TableCell>
                        <TableCell className="text-right tabular-nums">{failure.failures}</TableCell>
                        <TableCell className="truncate" title={failure.lastReason ?? undefined}>{failure.lastReason}</TableCell>
                        <TableCell>{formatTimestamp(failure.lastAt)}</TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              </div>
            </CardContent>
          </Card>
        </>
      )}
    </div>
  );
}
