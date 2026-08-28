import { useEffect, useMemo, useState } from 'react';
import { FileDown, PieChart } from 'lucide-react';
import { apiFetchWithRetry } from '../lib/apiClient.ts';
import { formatDateTime } from '../lib/utils.ts';
import { toCsv, downloadBlob } from '../lib/csv.ts';
import { Button } from '../components/ui/button.tsx';
import { Card, CardContent } from '../components/ui/card.tsx';
import { Input } from '../components/ui/input.tsx';
import { Label } from '../components/ui/label.tsx';
import { Skeleton } from '../components/ui/skeleton.tsx';
import { Badge, type BadgeProps } from '../components/ui/badge.tsx';
import { EmptyState } from '../components/ui/empty-state.tsx';
import { Table, TableHeader, TableBody, TableRow, TableHead, TableCell } from '../components/ui/table.tsx';

interface SessionHistoryRecord {
  sessionId: string;
  finalState: string;
  deviceSerialNumber: string;
  deviceManufacturer: string;
  deviceModel: string;
  createdAt: string;
  terminalAt: string;
}

/** Default report window: trailing 30 days. */
const DEFAULT_WINDOW_DAYS = 30;

function isoDateInputValue(date: Date): string {
  return date.toISOString().slice(0, 10);
}

function stateLabel(state: string): string {
  return state.replace('Session', '').replace(/([A-Z])/g, ' $1').trim();
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
 * Session Outcomes report (Reports feature): breakdown of terminal session outcomes over a
 * configurable date range, backed by the durable SessionHistory audit table (not the live,
 * purge-limited "DeviceSessions" table).
 */
export default function ReportSessionOutcomesPage(): React.ReactElement {
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

  const handleExport = () => {
    if (!records || records.length === 0) return;
    const csv = toCsv(records, [
      { header: 'Session ID', accessor: r => r.sessionId },
      { header: 'Final State', accessor: r => stateLabel(r.finalState) },
      { header: 'Device Serial Number', accessor: r => r.deviceSerialNumber },
      { header: 'Manufacturer', accessor: r => r.deviceManufacturer },
      { header: 'Model', accessor: r => r.deviceModel },
      { header: 'Created At', accessor: r => r.createdAt },
      { header: 'Terminal At', accessor: r => r.terminalAt },
      { header: 'Duration', accessor: r => formatDuration(r.createdAt, r.terminalAt) },
    ]);
    downloadBlob(`session-outcomes-${from}-to-${to}.csv`, csv);
  };

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-end justify-between gap-4">
        <div className="flex items-end gap-3">
          <div className="space-y-1.5">
            <Label htmlFor="from">From</Label>
            <Input id="from" type="date" value={from} max={to} onChange={e => setFrom(e.target.value)} className="w-40" />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="to">To</Label>
            <Input id="to" type="date" value={to} min={from} onChange={e => setTo(e.target.value)} className="w-40" />
          </div>
        </div>
        <Button variant="secondary" onClick={handleExport} disabled={!records || records.length === 0}>
          <FileDown size={14} /> Export CSV
        </Button>
      </div>

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
              <p className="mt-1 text-2xl font-semibold tabular-nums">
                {records ? stat.value : <Skeleton className="h-7 w-10" />}
              </p>
            </CardContent>
          </Card>
        ))}
      </div>

      {successRate !== null && (
        <p className="text-sm text-muted-foreground">
          Success rate over this period: <span className="font-medium text-foreground">{successRate}%</span>
        </p>
      )}

      <Table>
        <TableHeader>
          <TableRow>
            <TableHead>State</TableHead>
            <TableHead>Device</TableHead>
            <TableHead>Manufacturer / Model</TableHead>
            <TableHead>Created</TableHead>
            <TableHead>Terminal</TableHead>
            <TableHead>Duration</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {records === null && Array.from({ length: 4 }).map((_, i) => (
            <TableRow key={i}>
              <TableCell colSpan={6}><Skeleton className="h-5 w-full" /></TableCell>
            </TableRow>
          ))}
          {records?.length === 0 && (
            <TableRow>
              <TableCell colSpan={6}>
                <EmptyState icon={PieChart} title="No session history recorded yet."
                  description="Completed, failed, expired, and not-authorized sessions will appear here as they occur." />
              </TableCell>
            </TableRow>
          )}
          {records?.map(r => (
            <TableRow key={r.sessionId}>
              <TableCell><Badge variant={stateBadgeVariant(r.finalState)} dot>{stateLabel(r.finalState)}</Badge></TableCell>
              <TableCell className="font-mono text-xs">{r.deviceSerialNumber}</TableCell>
              <TableCell>{r.deviceManufacturer} {r.deviceModel}</TableCell>
              <TableCell>{formatDateTime(r.createdAt)}</TableCell>
              <TableCell>{formatDateTime(r.terminalAt)}</TableCell>
              <TableCell>{formatDuration(r.createdAt, r.terminalAt)}</TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </div>
  );
}
