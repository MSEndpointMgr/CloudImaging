import { useEffect, useMemo, useState } from 'react';
import { FileDown, AlertOctagon } from 'lucide-react';
import { apiFetchWithRetry } from '../lib/apiClient.ts';
import { formatDateTime } from '../lib/utils.ts';
import { toCsv, downloadBlob } from '../lib/csv.ts';
import { Button } from '../components/ui/button.tsx';
import { Input } from '../components/ui/input.tsx';
import { Label } from '../components/ui/label.tsx';
import { TableSkeletonRows } from '../components/ui/skeleton.tsx';
import { Badge, type BadgeProps } from '../components/ui/badge.tsx';
import { EmptyState } from '../components/ui/empty-state.tsx';
import { CopyableId } from '../components/ui/copyable-id.tsx';
import { Table, TableHeader, TableBody, TableRow, TableHead, TableCell } from '../components/ui/table.tsx';

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
const DEFAULT_WINDOW_DAYS = 30;

function isoDateInputValue(date: Date): string {
  return date.toISOString().slice(0, 10);
}

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
  const today = useMemo(() => new Date(), []);
  const defaultFrom = useMemo(() => new Date(today.getTime() - DEFAULT_WINDOW_DAYS * 86_400_000), [today]);
  const [from, setFrom] = useState(isoDateInputValue(defaultFrom));
  const [to, setTo] = useState(isoDateInputValue(today));
  const [records, setRecords] = useState<SessionHistoryRecord[] | null>(null);

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

  const handleExport = () => {
    if (!records || records.length === 0) return;
    const csv = toCsv(records, [
      { header: 'Session ID', accessor: r => r.sessionId },
      { header: 'Final State', accessor: r => stateLabel(r.finalState) },
      { header: 'Device Serial Number', accessor: r => r.deviceSerialNumber },
      { header: 'Manufacturer', accessor: r => r.deviceManufacturer },
      { header: 'Model', accessor: r => r.deviceModel },
      { header: 'Failed Step', accessor: r => stepLabel(r.failedStepName) },
      { header: 'Error Detail', accessor: r => r.errorDetail ?? '' },
      { header: 'Created At', accessor: r => r.createdAt },
      { header: 'Terminal At', accessor: r => r.terminalAt },
    ]);
    downloadBlob(`failure-detail-${from}-to-${to}.csv`, csv);
  };

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-end justify-between gap-4">
        <div className="flex items-end gap-3">
          <div className="space-y-2">
            <Label htmlFor="from">From</Label>
            <Input id="from" type="date" value={from} max={to} onChange={e => setFrom(e.target.value)} className="w-40" />
          </div>
          <div className="space-y-2">
            <Label htmlFor="to">To</Label>
            <Input id="to" type="date" value={to} min={from} onChange={e => setTo(e.target.value)} className="w-40" />
          </div>
        </div>
        <Button variant="secondary" onClick={handleExport} disabled={!records || records.length === 0}>
          <FileDown /> Export CSV
        </Button>
      </div>

      <div className="rounded-md border border-border overflow-hidden">
      <Table>
        <TableHeader>
          <TableRow className="hover:bg-transparent">
            <TableHead>State</TableHead>
            <TableHead>Device</TableHead>
            <TableHead>Failed Step</TableHead>
            <TableHead>Error Detail</TableHead>
            <TableHead>Terminal At</TableHead>
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
          {records?.map(r => (
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
