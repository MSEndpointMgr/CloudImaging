// Full Sessions page implementation, see import below
export default function SessionsPage(): React.ReactElement {
  return <SessionsPageImpl />;
}

import { useState, useEffect, useCallback, useRef, useMemo } from 'react';
import { Link } from 'react-router-dom';
import { RefreshCw, FileDown, ArrowUp, ArrowDown, ArrowUpDown, AlertTriangle } from 'lucide-react';
import { apiFetch, apiFetchWithRetry } from '../lib/apiClient.ts';
import { Button } from '../components/ui/button.tsx';
import { Input } from '../components/ui/input.tsx';
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
  createdAt: string;
}

interface SessionLogEntry {
  fileName: string;
  sizeBytes: number;
  uploadedAt: string;
}

interface OsImage {
  imageId: string;
  name: string;
  version: string;
  isActive: boolean;
}

// States for which the Client may have uploaded a diagnostic log on failure.
const FAILED_STATES = new Set(['SessionFailed', 'SessionNotAuthorized']);

type DeviceView = 'pending' | 'monitor';

// Available: newly-registered sessions awaiting a technician to enter the device's passcode.
const AVAILABLE_STATES = new Set(['SessionInit', 'SessionAllowed']);
// Coupled: passcode has been matched — eligible for OS image selection.
const COUPLED_STATES = new Set(['SessionAssigned']);
// Monitor: devices whose imaging has started, completed, failed, or were never authorized.
const MONITOR_STATES = new Set(['SessionStarted', 'SessionInProgress', 'SessionCompleted', 'SessionFailed', 'SessionNotAuthorized']);

function deriveCounts(sessions: Session[]) {
  return {
    pending: sessions.filter(s => AVAILABLE_STATES.has(s.state) || COUPLED_STATES.has(s.state)).length,
    monitor: sessions.filter(s => MONITOR_STATES.has(s.state)).length,
  };
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

function formatRegistered(iso: string): string {
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return '-';
  return date.toLocaleString(undefined, { dateStyle: 'short', timeStyle: 'short' });
}

// ── Generic column sorting ────────────────────────────────────────────────

type SortDir = 'asc' | 'desc';
interface SortState<K extends string> {
  key: K;
  dir: SortDir;
}

function useSort<K extends string>(defaultKey: K): [SortState<K>, (key: K) => void] {
  const [sort, setSort] = useState<SortState<K>>({ key: defaultKey, dir: 'asc' });
  const toggle = useCallback((key: K) => {
    setSort(prev => (prev.key === key ? { key, dir: prev.dir === 'asc' ? 'desc' : 'asc' } : { key, dir: 'asc' }));
  }, []);
  return [sort, toggle];
}

function sortRows<T, K extends string>(
  rows: T[],
  sort: SortState<K>,
  accessors: Record<K, (row: T) => string | number>,
): T[] {
  const accessor = accessors[sort.key];
  return [...rows].sort((a, b) => {
    const av = accessor(a);
    const bv = accessor(b);
    const cmp = typeof av === 'number' && typeof bv === 'number'
      ? av - bv
      : String(av).localeCompare(String(bv), undefined, { sensitivity: 'base' });
    return sort.dir === 'asc' ? cmp : -cmp;
  });
}

function SortableHead<K extends string>({ label, sortKey, sort, onSort, className }: {
  label: string;
  sortKey: K;
  sort: SortState<K>;
  onSort: (key: K) => void;
  className?: string;
}): React.ReactElement {
  const active = sort.key === sortKey;
  const Icon = active ? (sort.dir === 'asc' ? ArrowUp : ArrowDown) : ArrowUpDown;
  return (
    <TableHead className={className}>
      <button
        type="button"
        onClick={() => onSort(sortKey)}
        className="inline-flex items-center gap-1 hover:text-foreground focus-visible:outline-none"
      >
        {label}
        <Icon size={12} className={active ? '' : 'opacity-30'} />
      </button>
    </TableHead>
  );
}

// ── Inline passcode coupling (per Available row) ──────────────────────────

function PasscodeCouplingCell({ onCoupled }: { onCoupled: () => void }): React.ReactElement {
  const [passcode, setPasscode] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const trimmed = passcode.trim();
    if (trimmed.length !== 6) return;

    let cancelled = false;
    setBusy(true);
    setError(null);
    void (async () => {
      try {
        const res = await apiFetch('/api/sessions/couple', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          credentials: 'include',
          body: JSON.stringify({ passcode: trimmed }),
        });
        if (cancelled) return;
        if (res.status === 201) {
          setPasscode('');
          onCoupled();
          return;
        }
        setError(res.status === 409 ? 'Already used.' : 'Invalid or expired.');
      } catch {
        if (!cancelled) setError('Network error.');
      } finally {
        if (!cancelled) setBusy(false);
      }
    })();

    return () => { cancelled = true; };
  // Re-validate only when the passcode itself changes; onCoupled is a stable callback.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [passcode]);

  return (
    <div className="flex flex-col items-end gap-1">
      <Input
        type="text"
        maxLength={6}
        placeholder="Passcode"
        value={passcode}
        onChange={e => { setPasscode(e.target.value.toUpperCase()); setError(null); }}
        disabled={busy}
        className="h-8 w-28 text-center font-mono text-xs tracking-widest"
      />
      {error && <span className="text-xs text-destructive">{error}</span>}
    </div>
  );
}

function SessionsPageImpl(): React.ReactElement {
  const { notify } = useToast();
  const [sessions, setSessions]         = useState<Session[]>([]);
  const [view, setView]                 = useState<DeviceView>('pending');
  const [loading, setLoading]           = useState(false);
  const [downloadingLog, setDownloadingLog] = useState<string | null>(null);
  const [images, setImages]             = useState<OsImage[]>([]);
  const [imagesLoaded, setImagesLoaded] = useState(false);
  const [selectedImageId, setSelectedImageId] = useState<string | null>(null);
  const [startingImages, setStartingImages]   = useState(false);
  const timerRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  // Imaging (and, by extension, the Start Imaging action) is impossible until at least one
  // active OS image has been uploaded — surfaced via a persistent banner rather than a toast
  // so technicians can't miss it while coupling devices ahead of an image being ready.
  const hasOsImages = images.length > 0;

  const [availableSort, toggleAvailableSort] = useSort<'serial' | 'device' | 'state' | 'registered'>('registered');
  const [coupledSort, toggleCoupledSort]     = useSort<'serial' | 'device' | 'registered'>('registered');
  const [monitorSort, toggleMonitorSort]     = useSort<'serial' | 'device' | 'state' | 'progress' | 'step'>('state');

  useEffect(() => {
    void (async () => {
      try {
        const res = await apiFetch('/api/images', { credentials: 'include' });
        if (res.ok) {
          const data = await res.json() as OsImage[];
          setImages(data.filter(i => i.isActive));
        }
      } catch { /* leave list empty */ }
      finally { setImagesLoaded(true); }
    })();
  }, []);

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
      const res = await apiFetchWithRetry('/api/sessions', { credentials: 'include' });
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

  const counts    = deriveCounts(sessions);
  const available = useMemo(() => sortRows(
    sessions.filter(s => AVAILABLE_STATES.has(s.state)),
    availableSort,
    {
      serial:     (s: Session) => s.deviceSerialNumber,
      device:     (s: Session) => `${s.deviceManufacturer} ${s.deviceModel}`,
      state:      (s: Session) => stateLabel(s.state),
      registered: (s: Session) => new Date(s.createdAt).getTime(),
    },
  ), [sessions, availableSort]);
  const coupled = useMemo(() => sortRows(
    sessions.filter(s => COUPLED_STATES.has(s.state)),
    coupledSort,
    {
      serial:     (s: Session) => s.deviceSerialNumber,
      device:     (s: Session) => `${s.deviceManufacturer} ${s.deviceModel}`,
      registered: (s: Session) => new Date(s.createdAt).getTime(),
    },
  ), [sessions, coupledSort]);
  const monitor = useMemo(() => sortRows(
    sessions.filter(s => MONITOR_STATES.has(s.state)),
    monitorSort,
    {
      serial:   (s: Session) => s.deviceSerialNumber,
      device:   (s: Session) => `${s.deviceManufacturer} ${s.deviceModel}`,
      state:    (s: Session) => stateLabel(s.state),
      progress: (s: Session) => s.overallProgressPercent,
      step:     (s: Session) => s.currentStep ?? '',
    },
  ), [sessions, monitorSort]);

  const handleRefresh = () => {
    void (async () => { const data = await fetchSessions(); scheduleNextPoll(data); })();
  };

  const handleStartImaging = async () => {
    if (!hasOsImages) {
      notify({ status: 'error', title: 'Upload an OS image before starting imaging.' });
      return;
    }
    if (!selectedImageId || coupled.length === 0 || startingImages) return;
    setStartingImages(true);
    try {
      const res = await apiFetch('/api/sessions/bulk-assign', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        credentials: 'include',
        body: JSON.stringify({ sessionIds: coupled.map(s => s.sessionId), osImageId: selectedImageId }),
      });
      if (res.ok) {
        const data = await res.json() as { assigned: number };
        notify({ status: 'success', title: `Imaging started for ${data.assigned} device${data.assigned !== 1 ? 's' : ''}.` });
        setSelectedImageId(null);
        handleRefresh();
      } else {
        notify({ status: 'error', title: 'Failed to start imaging. Please try again.' });
      }
    } catch {
      notify({ status: 'error', title: 'Network error. Please try again.' });
    } finally {
      setStartingImages(false);
    }
  };

  return (
    <div className="space-y-5">
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
                onClick={() => setView(tab.key)}
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
        <Button variant="outline" size="sm" onClick={handleRefresh}>
          <RefreshCw className={loading ? 'animate-spin' : ''} /> Refresh
        </Button>
      </div>

      <p className="text-sm text-muted-foreground">Enter a device&apos;s passcode to couple it, assign an OS image to coupled devices, and monitor deployment progress and status in real time.</p>

      {view === 'pending' ? (
        <div className="space-y-5">
          {imagesLoaded && !hasOsImages && (
            <div className="flex items-start gap-2 rounded-md border border-amber-500/30 bg-amber-500/10 px-3 py-2 text-xs text-amber-600 dark:text-amber-400">
              <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0" aria-hidden="true" />
              <p>
                No OS images have been uploaded yet. Devices can still be coupled, but imaging cannot start until
                you <Link to="/os-images" className="font-medium underline underline-offset-2">upload an OS image</Link>.
              </p>
            </div>
          )}
          <div className="rounded-md border border-border overflow-hidden">
            <div className="flex items-center gap-2 border-b border-border px-4 py-3">
              <h3 className="text-sm font-semibold">Available Devices</h3>
              <span className="inline-flex min-w-5 items-center justify-center rounded-full bg-muted px-1.5 py-0.5 text-xs font-semibold text-muted-foreground">
                {available.length}
              </span>
            </div>
            <Table className="table-fixed">
              <TableHeader>
                <TableRow className="hover:bg-transparent">
                  <SortableHead label="Serial" sortKey="serial" sort={availableSort} onSort={toggleAvailableSort} className="w-[22%]" />
                  <SortableHead label="Device" sortKey="device" sort={availableSort} onSort={toggleAvailableSort} className="w-[26%]" />
                  <SortableHead label="State" sortKey="state" sort={availableSort} onSort={toggleAvailableSort} className="w-[14%]" />
                  <SortableHead label="Registered" sortKey="registered" sort={availableSort} onSort={toggleAvailableSort} className="w-[16%]" />
                  <TableHead className="w-[22%] text-right">Passcode</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {loading && sessions.length === 0 ? (
                  Array.from({ length: 3 }).map((_, i) => (
                    <TableRow key={`av-skeleton-${i}`} className="hover:bg-transparent">
                      <TableCell><Skeleton className="h-4 w-24" /></TableCell>
                      <TableCell><Skeleton className="h-4 w-32" /></TableCell>
                      <TableCell><Skeleton className="h-5 w-20 rounded-full" /></TableCell>
                      <TableCell><Skeleton className="h-4 w-24" /></TableCell>
                      <TableCell className="text-right"><Skeleton className="ml-auto h-8 w-32" /></TableCell>
                    </TableRow>
                  ))
                ) : available.length === 0 ? (
                  <TableRow className="hover:bg-transparent">
                    <TableCell colSpan={5} className="select-none py-12 text-center text-muted-foreground">
                      No devices waiting to be coupled.
                    </TableCell>
                  </TableRow>
                ) : available.map(s => (
                  <TableRow key={s.sessionId}>
                    <TableCell className="font-mono text-xs">{s.deviceSerialNumber}</TableCell>
                    <TableCell>{s.deviceManufacturer} {s.deviceModel}</TableCell>
                    <TableCell><Badge variant={stateBadgeVariant(s.state)} dot>{stateLabel(s.state)}</Badge></TableCell>
                    <TableCell className="text-xs text-muted-foreground">{formatRegistered(s.createdAt)}</TableCell>
                    <TableCell className="text-right">
                      <PasscodeCouplingCell onCoupled={handleRefresh} />
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </div>

          <div className="rounded-md border border-border overflow-hidden">
            <div className="flex flex-wrap items-center justify-between gap-3 border-b border-border px-4 py-3">
              <div className="flex items-center gap-2">
                <h3 className="text-sm font-semibold">Coupled Devices</h3>
                <span className="inline-flex min-w-5 items-center justify-center rounded-full bg-primary/15 px-1.5 py-0.5 text-xs font-semibold text-primary">
                  {coupled.length}
                </span>
              </div>
              <div className="flex min-w-0 flex-1 items-center justify-end gap-2">
                <select
                  value={selectedImageId ?? ''}
                  onChange={e => setSelectedImageId(e.target.value || null)}
                  disabled={coupled.length === 0 || images.length === 0}
                  title={!hasOsImages ? 'Upload an OS image before assigning one to coupled devices.' : undefined}
                  className="h-8 w-40 min-w-0 max-w-full flex-1 truncate rounded-md border border-input bg-background px-2 text-sm shadow-sm disabled:cursor-not-allowed disabled:opacity-50 sm:w-64 sm:flex-none"
                >
                  <option value="">{hasOsImages ? 'Select OS image…' : 'No OS images uploaded'}</option>
                  {images.map(img => (
                    <option key={img.imageId} value={img.imageId}>{img.name} {img.version}</option>
                  ))}
                </select>
                <Button
                  size="sm"
                  onClick={() => { void handleStartImaging(); }}
                  disabled={!selectedImageId || coupled.length === 0 || startingImages}
                  title={!hasOsImages ? 'Upload an OS image before starting imaging.' : undefined}
                >
                  {startingImages ? 'Starting…' : `Start Imaging (${coupled.length})`}
                </Button>
              </div>
            </div>
            <Table className="table-fixed">
              <TableHeader>
                <TableRow className="hover:bg-transparent">
                  <SortableHead label="Serial" sortKey="serial" sort={coupledSort} onSort={toggleCoupledSort} className="w-[30%]" />
                  <SortableHead label="Device" sortKey="device" sort={coupledSort} onSort={toggleCoupledSort} className="w-[40%]" />
                  <SortableHead label="Registered" sortKey="registered" sort={coupledSort} onSort={toggleCoupledSort} className="w-[30%]" />
                </TableRow>
              </TableHeader>
              <TableBody>
                {coupled.length === 0 ? (
                  <TableRow className="hover:bg-transparent">
                    <TableCell colSpan={3} className="select-none py-12 text-center text-muted-foreground">
                      No devices ready for imaging.
                    </TableCell>
                  </TableRow>
                ) : coupled.map(s => (
                  <TableRow key={s.sessionId}>
                    <TableCell className="font-mono text-xs">{s.deviceSerialNumber}</TableCell>
                    <TableCell>{s.deviceManufacturer} {s.deviceModel}</TableCell>
                    <TableCell className="text-xs text-muted-foreground">{formatRegistered(s.createdAt)}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </div>
        </div>
      ) : (
        <div className="rounded-md border border-border overflow-hidden">
          <Table className="table-fixed">
            <TableHeader>
              <TableRow className="hover:bg-transparent">
                <SortableHead label="Serial" sortKey="serial" sort={monitorSort} onSort={toggleMonitorSort} className="w-[16%]" />
                <SortableHead label="Device" sortKey="device" sort={monitorSort} onSort={toggleMonitorSort} className="w-[22%]" />
                <SortableHead label="State" sortKey="state" sort={monitorSort} onSort={toggleMonitorSort} className="w-[13%]" />
                <SortableHead label="Progress" sortKey="progress" sort={monitorSort} onSort={toggleMonitorSort} className="w-[19%]" />
                <SortableHead label="Step" sortKey="step" sort={monitorSort} onSort={toggleMonitorSort} className="w-[16%]" />
                <TableHead className="text-right">Actions</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {loading && sessions.length === 0 ? (
                Array.from({ length: 5 }).map((_, i) => (
                  <TableRow key={`mon-skeleton-${i}`} className="hover:bg-transparent">
                    <TableCell><Skeleton className="h-4 w-24" /></TableCell>
                    <TableCell><Skeleton className="h-4 w-32" /></TableCell>
                    <TableCell><Skeleton className="h-5 w-20 rounded-full" /></TableCell>
                    <TableCell><Skeleton className="h-4 w-28" /></TableCell>
                    <TableCell><Skeleton className="h-4 w-24" /></TableCell>
                    <TableCell className="text-right"><Skeleton className="ml-auto h-8 w-24" /></TableCell>
                  </TableRow>
                ))
              ) : monitor.length === 0 ? (
                <TableRow className="hover:bg-transparent">
                  <TableCell colSpan={6} className="py-12 text-center text-muted-foreground">
                    No imaging activity yet.
                  </TableCell>
                </TableRow>
              ) : monitor.map(s => (
                <TableRow key={s.sessionId}>
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
      )}
    </div>
  );
}
