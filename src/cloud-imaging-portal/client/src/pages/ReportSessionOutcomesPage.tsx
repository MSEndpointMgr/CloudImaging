import { useEffect, useMemo, useState } from 'react';
import { FileDown, PieChart } from 'lucide-react';
import { apiFetchWithRetry } from '../lib/apiClient.ts';
import { formatDateTime } from '../lib/utils.ts';
import { toCsv, downloadBlob } from '../lib/csv.ts';
import { resolvePreset, type DateRange } from '../lib/dateRange.ts';
import { sortRows, useSort } from '../lib/tableSort.ts';
import { Button } from '../components/ui/button.tsx';
import { Card, CardContent } from '../components/ui/card.tsx';
import { DateRangeSelect } from '../components/ui/date-range-select.tsx';
import { ReportFilterBar } from '../components/ReportFilterBar.tsx';
import { SortableHead } from '../components/ui/sortable-head.tsx';
import { Skeleton, TableSkeletonRows } from '../components/ui/skeleton.tsx';
import { Badge, type BadgeProps } from '../components/ui/badge.tsx';
import { CopyableId } from '../components/ui/copyable-id.tsx';
import { EmptyState } from '../components/ui/empty-state.tsx';
import { Table, TableHeader, TableBody, TableRow, TableCell } from '../components/ui/table.tsx';

interface SessionHistoryRecord {
  sessionId: string;
  finalState: string;
  deviceSerialNumber: string;
  deviceManufacturer: string;
  deviceModel: string;
  createdAt: string;
  terminalAt: string;
}

/**
 * Turns a `SessionState` name into a display label. Defensively coerces rather than assuming a
 * string: this runs inside render, so a single unexpected value (a backend that serialized the
 * enum as an ordinal, say) would otherwise throw and replace the entire report with the route
 * error boundary instead of degrading one cell.
 */
function stateLabel(state: string): string {
  if (state === null || state === undefined) return '-';
  return String(state).replace('Session', '').replace(/([A-Z])/g, ' $1').trim();
}

function stateBadgeVariant(state: string): BadgeProps['variant'] {
  switch (state) {
    case 'SessionCompleted':     return 'success';
    case 'SessionFailed':        return 'destructive';
    case 'SessionNotAuthorized': return 'warning';
    case 'SessionExpired':       return 'muted';
    default:                     return 'muted';
  }
}

function formatDuration(fromIso: string, toIso: string): string {
  const ms = new Date(toIso).getTime() - new Date(fromIso).getTime();
  if (!Number.isFinite(ms) || ms < 0) return '-';
  const totalSeconds = Math.round(ms / 1000);
  if (totalSeconds < 60) return `${totalSeconds}s`;
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;
  if (minutes < 60) return `${minutes}m ${seconds}s`;
  const hours = Math.floor(minutes / 60);
  return `${hours}h ${minutes % 60}m`;
}

/**
 * Device Outcomes report (Reports feature): breakdown of terminal device outcomes over a
 * configurable date range, backed by the durable SessionHistory audit table (not the live,
 * purge-limited "DeviceSessions" table).
 */
export default function ReportDeviceOutcomesPage(): React.ReactElement {
  const [range, setRange] = useState<DateRange>(() => resolvePreset('last30')!);
  const [records, setRecords] = useState<SessionHistoryRecord[] | null>(null);
  // The API returns rows in table-storage enumeration order, which is not chronological, so the
  // report has to impose one.
  const [sort, toggleSort] = useSort<'state' | 'device' | 'model' | 'started' | 'finished' | 'duration'>({ key: 'finished', dir: 'desc' });
  const { from, to } = range;

  useEffect(() => {
    let cancelled = false;
    setRecords(null);
    void (async () => {
      try {
        const params = new URLSearchParams({
          from: new Date(`${from}T00:00:00Z`).toISOString(),
          to: new Date(`${to}T23:59:59Z`).toISOString(),
        });
        const res = await apiFetchWithRetry(`/api/session-history?${params.toString()}`, { credentials: 'include' });
        if (cancelled) return;
        setRecords(res.ok ? await res.json() as SessionHistoryRecord[] : []);
      } catch {
        if (!cancelled) setRecords([]);
      }
    })();
    return () => { cancelled = true; };
  }, [from, to]);

  const counts = useMemo(() => {
    const c = { completed: 0, failed: 0, expired: 0, notAuthorized: 0, total: 0 };
    for (const r of records ?? []) {
      c.total++;
      if (r.finalState === 'SessionCompleted') c.completed++;
      else if (r.finalState === 'SessionFailed') c.failed++;
      else if (r.finalState === 'SessionExpired') c.expired++;
      else if (r.finalState === 'SessionNotAuthorized') c.notAuthorized++;
    }
    return c;
  }, [records]);

  const successRate = counts.total > 0 ? Math.round((counts.completed / counts.total) * 100) : null;

  const sorted = useMemo(() => sortRows(records ?? [], sort, {
    state:    (r: SessionHistoryRecord) => stateLabel(r.finalState),
    device:   (r: SessionHistoryRecord) => r.deviceSerialNumber,
    model:    (r: SessionHistoryRecord) => `${r.deviceManufacturer} ${r.deviceModel}`,
    started:  (r: SessionHistoryRecord) => new Date(r.createdAt).getTime(),
    finished: (r: SessionHistoryRecord) => new Date(r.terminalAt).getTime(),
    duration: (r: SessionHistoryRecord) => new Date(r.terminalAt).getTime() - new Date(r.createdAt).getTime(),
  }), [records, sort]);

  const handleExport = () => {
    if (sorted.length === 0) return;
    const csv = toCsv(sorted, [
      { header: 'Session ID', accessor: r => r.sessionId },
      { header: 'Final State', accessor: r => stateLabel(r.finalState) },
      { header: 'Device Serial Number', accessor: r => r.deviceSerialNumber },
      { header: 'Manufacturer', accessor: r => r.deviceManufacturer },
      { header: 'Model', accessor: r => r.deviceModel },
      { header: 'Started At', accessor: r => r.createdAt },
      { header: 'Finished At', accessor: r => r.terminalAt },
      { header: 'Duration', accessor: r => formatDuration(r.createdAt, r.terminalAt) },
    ]);
    downloadBlob(`device-outcomes-${from}-to-${to}.csv`, csv);
  };

  return (
    <div className="space-y-6">
      <ReportFilterBar
        actions={
          <Button variant="outline" onClick={handleExport} disabled={!records || records.length === 0}>
            <FileDown /> Export CSV
          </Button>
        }
      >
        <DateRangeSelect
          id="device-outcomes-range"
          aria-label="Date range"
          value={range}
          onValueChange={setRange}
          wrapperClassName="w-44"
        />
      </ReportFilterBar>

      <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-5">
        {[
          { label: 'Total sessions', value: counts.total },
          { label: 'Completed', value: counts.completed },
          { label: 'Failed', value: counts.failed },
          { label: 'Expired', value: counts.expired },
          { label: 'Not authorized', value: counts.notAuthorized },
        ].map(stat => (
          <Card key={stat.label}>
            <CardContent className="p-4">
              <p className="text-xs text-muted-foreground">{stat.label}</p>
              <div className="mt-1 text-2xl font-semibold tabular-nums">
                {records ? stat.value : <Skeleton className="h-7 w-10" />}
              </div>
            </CardContent>
          </Card>
        ))}
      </div>

      {successRate !== null && (
        <p className="text-sm text-muted-foreground">
          Success rate over this period: <span className="font-medium text-foreground">{successRate}%</span>
        </p>
      )}

      <div className="rounded-md border border-border overflow-hidden">
      <Table>
        <TableHeader>
          <TableRow className="hover:bg-transparent">
            <SortableHead label="State" sortKey="state" sort={sort} onSort={toggleSort} />
            <SortableHead label="Device" sortKey="device" sort={sort} onSort={toggleSort} />
            <SortableHead label="Manufacturer / Model" sortKey="model" sort={sort} onSort={toggleSort} />
            <SortableHead label="Started" sortKey="started" sort={sort} onSort={toggleSort} />
            <SortableHead label="Finished" sortKey="finished" sort={sort} onSort={toggleSort} />
            <SortableHead label="Duration" sortKey="duration" sort={sort} onSort={toggleSort} />
          </TableRow>
        </TableHeader>
        <TableBody>
          {records === null && <TableSkeletonRows columns={6} rows={4} />}
          {records?.length === 0 && (
            <TableRow>
              <TableCell colSpan={6}>
                <EmptyState icon={PieChart} title="No session history recorded yet."
                  description="Completed, failed, expired, and not-authorized sessions will appear here as they occur." />
              </TableCell>
            </TableRow>
          )}
          {sorted.map(r => (
            <TableRow key={r.sessionId}>
              <TableCell><Badge variant={stateBadgeVariant(r.finalState)} dot>{stateLabel(r.finalState)}</Badge></TableCell>
              <TableCell>
                <CopyableId value={r.deviceSerialNumber} label="device serial number" className="font-mono text-sm font-medium text-foreground" />
              </TableCell>
              <TableCell>{r.deviceManufacturer} {r.deviceModel}</TableCell>
              <TableCell>{formatDateTime(r.createdAt)}</TableCell>
              <TableCell>{formatDateTime(r.terminalAt)}</TableCell>
              <TableCell>{formatDuration(r.createdAt, r.terminalAt)}</TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
      </div>
    </div>
  );
}
