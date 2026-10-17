import { useEffect, useMemo, useState } from 'react';
import { FileDown, AlertOctagon } from 'lucide-react';
import { apiFetchWithRetry } from '../lib/apiClient.ts';
import { formatDateTime } from '../lib/utils.ts';
import { toCsv, downloadBlob } from '../lib/csv.ts';
import { resolvePreset, type DateRange } from '../lib/dateRange.ts';
import { sortRows, useSort } from '../lib/tableSort.ts';
import { Button } from '../components/ui/button.tsx';
import { DateRangeSelect } from '../components/ui/date-range-select.tsx';
import { ReportFilterBar } from '../components/ReportFilterBar.tsx';
import { SortableHead } from '../components/ui/sortable-head.tsx';
import { TableSkeletonRows } from '../components/ui/skeleton.tsx';
import { Badge, type BadgeProps } from '../components/ui/badge.tsx';
import { EmptyState } from '../components/ui/empty-state.tsx';
import { CopyableId } from '../components/ui/copyable-id.tsx';
import { Table, TableHeader, TableBody, TableRow, TableCell } from '../components/ui/table.tsx';

interface SessionHistoryRecord {
  sessionId: string;
  finalState: string;
  deviceSerialNumber: string;
  deviceManufacturer: string;
  deviceModel: string;
  failedStepName: string | null;
  errorDetail: string | null;
  createdAt: string;
  terminalAt: string;
}

const FAILURE_STATES = new Set(['SessionFailed', 'SessionNotAuthorized', 'SessionExpired']);

/** Display label for a `SessionState` name. Total, for the reason given on the same helper in ReportSessionOutcomesPage. */
function stateLabel(state: string): string {
  if (state === null || state === undefined) return '-';
  return String(state).replace('Session', '').replace(/([A-Z])/g, ' $1').trim();
}

function stateBadgeVariant(state: string): BadgeProps['variant'] {
  switch (state) {
    case 'SessionFailed':        return 'destructive';
    case 'SessionNotAuthorized': return 'warning';
    case 'SessionExpired':       return 'muted';
    default:                     return 'muted';
  }
}

function stepLabel(step: string | null): string {
  if (!step) return '-';
  return String(step).replace(/([A-Z])/g, ' $1').trim();
}

/**
 * Failure Detail report (Reports feature): every failed, expired, or not-authorized session
 * over a date range, with its failed step and error detail — for diagnosing recurring issues.
 */
export default function ReportFailureDetailPage(): React.ReactElement {
  const [range, setRange] = useState<DateRange>(() => resolvePreset('last30')!);
  const [records, setRecords] = useState<SessionHistoryRecord[] | null>(null);
  const [sort, toggleSort] = useSort<'state' | 'device' | 'step' | 'error' | 'finished'>({ key: 'finished', dir: 'desc' });
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
        const all = res.ok ? await res.json() as SessionHistoryRecord[] : [];
        setRecords(all.filter(r => FAILURE_STATES.has(r.finalState)));
      } catch {
        if (!cancelled) setRecords([]);
      }
    })();
    return () => { cancelled = true; };
  }, [from, to]);

  const sorted = useMemo(() => sortRows(records ?? [], sort, {
    state:    (r: SessionHistoryRecord) => stateLabel(r.finalState),
    device:   (r: SessionHistoryRecord) => r.deviceSerialNumber,
    step:     (r: SessionHistoryRecord) => stepLabel(r.failedStepName),
    error:    (r: SessionHistoryRecord) => r.errorDetail ?? '',
    finished: (r: SessionHistoryRecord) => new Date(r.terminalAt).getTime(),
  }), [records, sort]);

  // Exports what is on screen, in the order it is on screen.
  const handleExport = () => {
    const csv = toCsv(sorted, [
      { header: 'Session ID', accessor: r => r.sessionId },
      { header: 'Final State', accessor: r => stateLabel(r.finalState) },
      { header: 'Device Serial Number', accessor: r => r.deviceSerialNumber },
      { header: 'Manufacturer', accessor: r => r.deviceManufacturer },
      { header: 'Model', accessor: r => r.deviceModel },
      { header: 'Failed Step', accessor: r => stepLabel(r.failedStepName) },
      { header: 'Error Detail', accessor: r => r.errorDetail ?? '' },
      { header: 'Started At', accessor: r => r.createdAt },
      { header: 'Finished At', accessor: r => r.terminalAt },
    ]);
    downloadBlob(`failure-detail-${from}-to-${to}.csv`, csv);
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
          id="failure-detail-range"
          aria-label="Date range"
          value={range}
          onValueChange={setRange}
          wrapperClassName="w-44"
        />
      </ReportFilterBar>

      <div className="rounded-md border border-border overflow-hidden">
      <Table>
        <TableHeader>
          <TableRow className="hover:bg-transparent">
            <SortableHead label="State" sortKey="state" sort={sort} onSort={toggleSort} />
            <SortableHead label="Device" sortKey="device" sort={sort} onSort={toggleSort} />
            <SortableHead label="Failed step" sortKey="step" sort={sort} onSort={toggleSort} />
            <SortableHead label="Error detail" sortKey="error" sort={sort} onSort={toggleSort} />
            <SortableHead label="Finished" sortKey="finished" sort={sort} onSort={toggleSort} />
          </TableRow>
        </TableHeader>
        <TableBody>
          {records === null && <TableSkeletonRows columns={5} rows={4} />}
          {records?.length === 0 && (
            <TableRow>
              <TableCell colSpan={5}>
                <EmptyState icon={AlertOctagon} title="No failures in this period."
                  description="Failed, expired, and not-authorized sessions will appear here as they occur." />
              </TableCell>
            </TableRow>
          )}
          {sorted.map(r => (
            <TableRow key={r.sessionId}>
              <TableCell><Badge variant={stateBadgeVariant(r.finalState)} dot>{stateLabel(r.finalState)}</Badge></TableCell>
              <TableCell>
                <CopyableId value={r.deviceSerialNumber} label="device serial number" className="font-mono text-sm font-medium text-foreground" />
                <div className="text-xs text-muted-foreground">{r.deviceManufacturer} {r.deviceModel}</div>
              </TableCell>
              <TableCell>{stepLabel(r.failedStepName)}</TableCell>
              <TableCell className="max-w-md truncate" title={r.errorDetail ?? undefined}>{r.errorDetail ?? '-'}</TableCell>
              <TableCell>{formatDateTime(r.terminalAt)}</TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
      </div>
    </div>
  );
}
