// Full Sessions page implementation, see import below
export default function SessionsPage(): React.ReactElement {
  return <SessionsPageImpl />;
}

import { useState, useEffect, useCallback, useRef } from 'react';
import { RefreshCw, FileDown } from 'lucide-react';
import { apiFetch } from '../lib/apiClient.ts';
import { CoupleSessionDialog } from '../components/CoupleSessionDialog.tsx';
import { AssignImageDialog } from '../components/AssignImageDialog.tsx';
import { Button } from '../components/ui/button.tsx';
import { Badge, type BadgeProps } from '../components/ui/badge.tsx';
import { Skeleton } from '../components/ui/skeleton.tsx';
import { cn } from '../lib/utils.ts';
import { useToast } from '../context/toastContext.tsx';
import {
  Table,
  TableHeader,
  TableBody,
  TableRow,
  TableHead,
  TableCell,
} from '../components/ui/table.tsx';

interface Session {
  sessionId: string;
  state: string;
  deviceSerialNumber: string;
  deviceManufacturer: string;
  deviceModel: string;
  overallProgressPercent: number;
  currentStep: string | null;
}

interface SessionLogEntry {
  fileName: string;
  sizeBytes: number;
  uploadedAt: string;
}

// States for which the Client may have uploaded a diagnostic log on failure.
const FAILED_STATES = new Set(['SessionFailed', 'SessionNotAuthorized']);


type DeviceView = 'pending' | 'monitor';

// Pending: devices coupled/authorized but whose imaging has not started yet.
const PENDING_STATES = new Set(['SessionInit','SessionAllowed','SessionAssigned','SessionNotAuthorized']);
// Monitor: devices whose imaging has started, completed, or failed.
const MONITOR_STATES = new Set(['SessionStarted','SessionInProgress','SessionCompleted','SessionFailed']);

function deriveCounts(sessions: Session[]) {
  return {
    pending: sessions.filter(s => PENDING_STATES.has(s.state)).length,
    monitor: sessions.filter(s => MONITOR_STATES.has(s.state)).length,
  };
}

function applyView(sessions: Session[], view: DeviceView): Session[] {
  const states = view === 'pending' ? PENDING_STATES : MONITOR_STATES;
  return sessions.filter(s => states.has(s.state));
}

function stateLabel(state: string): string {
  return state.replace('Session', '').replace(/([A-Z])/g, ' $1').trim();
}

function stateBadgeVariant(state: string): BadgeProps['variant'] {
  switch (state) {
    case 'SessionCompleted':     return 'success';
    case 'SessionFailed':        return 'destructive';
    case 'SessionNotAuthorized': return 'warning';
    case 'SessionInProgress':
    case 'SessionStarted':       return 'info';
    default:                     return 'muted';
  }
}

function SessionsPageImpl(): React.ReactElement {
  const { notify } = useToast();
  const [sessions, setSessions]         = useState<Session[]>([]);
  const [view, setView]                 = useState<DeviceView>('pending');
  const [checked, setChecked]           = useState<Set<string>>(new Set());
  const [coupleOpen, setCoupleOpen]     = useState(false);
  const [assignOpen, setAssignOpen]     = useState(false);
  const [assignTarget, setAssignTarget] = useState<string | null>(null);
  const [loading, setLoading]           = useState(false);
  const [downloadingLog, setDownloadingLog] = useState<string | null>(null);
  const timerRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  const handleDownloadLog = useCallback(async (sessionId: string) => {
    setDownloadingLog(sessionId);
    try {
      const listRes = await apiFetch(`/api/sessions/${sessionId}/logs`, { credentials: 'include' });
      if (!listRes.ok) {
        notify({ status: 'error', title: 'Could not retrieve logs for this session.' });
        return;
      }
      const entries = await listRes.json() as SessionLogEntry[];
      if (entries.length === 0) {
        notify({ status: 'info', title: 'No log was uploaded for this session.' });
        return;
      }
      const latest = [...entries].sort((a, b) => b.uploadedAt.localeCompare(a.uploadedAt))[0];
      const urlRes = await apiFetch(
        `/api/sessions/${sessionId}/logs/${encodeURIComponent(latest.fileName)}/download-url`,
        { credentials: 'include' });
      if (!urlRes.ok) {
        notify({ status: 'error', title: 'Could not generate a download link for this log.' });
        return;
      }
      const { downloadUrl } = await urlRes.json() as { downloadUrl: string };
      window.open(downloadUrl, '_blank', 'noopener,noreferrer');
    } catch {
      notify({ status: 'error', title: 'Could not retrieve logs for this session.' });
    } finally {
      setDownloadingLog(null);
    }
  }, [notify]);

  const fetchSessions = useCallback(async (data?: Session[]) => {
    setLoading(true);
    try {
      const res = await apiFetch('/api/sessions', { credentials: 'include' });
      if (res.ok) {
        const fetched = await res.json() as Session[];
        setSessions(fetched);
        return fetched;
      }
    } catch { /* retain previous */ }
    finally { setLoading(false); }
    return data ?? [];
  }, []);

  const scheduleNextPoll = useCallback((data: Session[]) => {
    if (timerRef.current) clearTimeout(timerRef.current);
    const hasHot = data.some(s => s.state === 'SessionStarted' || s.state === 'SessionInProgress');
    timerRef.current = setTimeout(async () => {
      const next = await fetchSessions(data);
      scheduleNextPoll(next);
    }, hasHot ? 5_000 : 30_000);
  }, [fetchSessions]);

  useEffect(() => {
    void (async () => { const data = await fetchSessions(); scheduleNextPoll(data); })();
    return () => { if (timerRef.current) clearTimeout(timerRef.current); };
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const visible  = applyView(sessions, view);
  const counts   = deriveCounts(sessions);
  const changeView   = (next: DeviceView) => { setView(next); setChecked(new Set()); };
  const toggleRow    = (id: string) => setChecked(prev => { const n = new Set(prev); if (n.has(id)) { n.delete(id); } else { n.add(id); } return n; });
  const selectAll    = () => setChecked(new Set(visible.map(s => s.sessionId)));
  const deselectAll  = () => setChecked(new Set());
  const allSelected  = visible.length > 0 && visible.every(s => checked.has(s.sessionId));
  const someSelected = visible.some(s => checked.has(s.sessionId));
  const eligibleCount = [...checked].filter(id => sessions.find(s => s.sessionId === id)?.state === 'SessionAssigned').length;

  const handleRefresh = () => {
    void (async () => { const data = await fetchSessions(); scheduleNextPoll(data); })();
  };

  return (
    <div className="space-y-5">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <p className="text-sm text-muted-foreground">Couple a device to start imaging, assign OS images to waiting devices, and monitor deployment progress and status in real time.</p>
        </div>
        <div className="flex items-center gap-2">
          <Button variant="outline" size="sm" onClick={handleRefresh}>
            <RefreshCw className={loading ? 'animate-spin' : ''} /> Refresh
          </Button>
          <Button size="sm" onClick={() => setCoupleOpen(true)}>
            Couple Device
          </Button>
        </div>
      </div>

      <div className="flex flex-wrap items-center justify-between gap-3">
        <div className="inline-flex items-center gap-1 rounded-lg bg-muted p-1">
          {([
            { key: 'pending', label: 'Pending', count: counts.pending },
            { key: 'monitor', label: 'Monitor', count: counts.monitor },
          ] as const).map((tab) => {
            const isActive = tab.key === view;
            return (
              <button
                key={tab.key}
                onClick={() => changeView(tab.key)}
                className={cn(
                  'inline-flex items-center gap-1.5 rounded-md px-3 py-1.5 text-sm font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring',
                  isActive
                    ? 'bg-background text-foreground shadow-sm'
                    : 'text-muted-foreground hover:text-foreground',
                )}
              >
                {tab.label}
                <span
                  className={cn(
                    'inline-flex min-w-5 items-center justify-center rounded-full px-1.5 py-0.5 text-xs font-semibold',
                    isActive ? 'bg-primary text-primary-foreground' : 'bg-background/70 text-muted-foreground',
                  )}
                >
                  {tab.count}
                </span>
              </button>
            );
          })}
        </div>
      </div>

      {eligibleCount > 0 && (
        <div className="flex items-center justify-between rounded-lg border border-primary/30 bg-primary/10 px-4 py-2.5 text-sm">
          <span className="font-medium">{eligibleCount} Assigned session{eligibleCount !== 1 ? 's' : ''} selected</span>
          <Button size="sm" onClick={() => { setAssignTarget(null); setAssignOpen(true); }}>
            Bulk Assign Image
          </Button>
        </div>
      )}

      <div className="rounded-md border border-border overflow-hidden">
        <Table className="table-fixed">
            <TableHeader>
              <TableRow className="hover:bg-transparent">
                <TableHead className="w-10 px-4">
                  <input
                    type="checkbox"
                    role="checkbox"
                    aria-label={allSelected ? 'Deselect all sessions' : 'Select all sessions'}
                    checked={allSelected}
                    ref={el => { if (el) el.indeterminate = someSelected && !allSelected; }}
                    onChange={() => (allSelected ? deselectAll() : selectAll())}
                    disabled={visible.length === 0}
                    className="h-4 w-4 rounded border-input align-middle accent-primary disabled:opacity-40"
                  />
                </TableHead>
                <TableHead className="w-[16%]">Serial</TableHead>
                <TableHead className="w-[22%]">Device</TableHead>
                <TableHead className="w-[13%]">State</TableHead>
                <TableHead className="w-[19%]">Progress</TableHead>
                <TableHead className="w-[16%]">Step</TableHead>
                <TableHead className="text-right">Actions</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {loading && sessions.length === 0 ? (
                Array.from({ length: 5 }).map((_, i) => (
                  <TableRow key={`skeleton-${i}`} className="hover:bg-transparent">
                    <TableCell className="px-4"><Skeleton className="h-4 w-4 rounded" /></TableCell>
                    <TableCell><Skeleton className="h-4 w-24" /></TableCell>
                    <TableCell><Skeleton className="h-4 w-32" /></TableCell>
                    <TableCell><Skeleton className="h-5 w-20 rounded-full" /></TableCell>
                    <TableCell><Skeleton className="h-4 w-28" /></TableCell>
                    <TableCell><Skeleton className="h-4 w-24" /></TableCell>
                    <TableCell className="text-right"><Skeleton className="ml-auto h-8 w-24" /></TableCell>
                  </TableRow>
                ))
              ) : visible.length === 0 ? (
                <TableRow className="hover:bg-transparent">
                  <TableCell colSpan={7} className="py-12 text-center text-muted-foreground">
                    {view === 'pending' ? 'No devices waiting to be imaged.' : 'No imaging activity yet.'}
                  </TableCell>
                </TableRow>
              ) : visible.map(s => (
                <TableRow key={s.sessionId} data-state={checked.has(s.sessionId) ? 'selected' : undefined}>
                  <TableCell className="px-4">
                    <input
                      type="checkbox"
                      role="checkbox"
                      checked={checked.has(s.sessionId)}
                      onChange={() => toggleRow(s.sessionId)}
                      className="h-4 w-4 rounded border-input align-middle accent-primary"
                    />
                  </TableCell>
                  <TableCell className="font-mono text-xs">{s.deviceSerialNumber}</TableCell>
                  <TableCell>{s.deviceManufacturer} {s.deviceModel}</TableCell>
                  <TableCell><Badge variant={stateBadgeVariant(s.state)} dot>{stateLabel(s.state)}</Badge></TableCell>
                  <TableCell>
                    {s.overallProgressPercent > 0 ? (
                      <div className="flex items-center gap-2">
                        <div className="h-1.5 w-24 overflow-hidden rounded-full bg-muted">
                          <div className="h-full rounded-full bg-primary transition-all" style={{ width: `${s.overallProgressPercent}%` }} />
                        </div>
                        <span className="text-xs text-muted-foreground">{s.overallProgressPercent}%</span>
                      </div>
                    ) : <span className="text-muted-foreground">-</span>}
                  </TableCell>
                  <TableCell className="text-xs text-muted-foreground">{s.currentStep ?? '-'}</TableCell>
                  <TableCell className="text-right">
                    {s.state === 'SessionAssigned' && (
                      <Button
                        variant="secondary"
                        size="sm"
                        onClick={() => { setAssignTarget(s.sessionId); setAssignOpen(true); }}
                      >
                        Assign Image
                      </Button>
                    )}
                    {FAILED_STATES.has(s.state) && (
                      <Button
                        variant="outline"
                        size="sm"
                        disabled={downloadingLog === s.sessionId}
                        onClick={() => { void handleDownloadLog(s.sessionId); }}
                      >
                        <FileDown className={downloadingLog === s.sessionId ? 'animate-pulse' : ''} /> Download log
                      </Button>
                    )}
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
      </div>

      <CoupleSessionDialog open={coupleOpen} onClose={() => setCoupleOpen(false)} onCoupled={() => handleRefresh()} />
      <AssignImageDialog
        open={assignOpen}
        sessionId={assignTarget ?? (checked.size > 0 ? [...checked][0] : null)}
        onClose={() => { setAssignOpen(false); setAssignTarget(null); }}
        onAssigned={() => handleRefresh()}
      />
    </div>
  );
}
