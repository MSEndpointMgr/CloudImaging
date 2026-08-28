// Full Sessions page implementation, see import below
export default function SessionsPage(): React.ReactElement {
  return <SessionsPageImpl />;
}

import { useState, useEffect, useCallback, useRef, useMemo, useId } from 'react';
import { Link } from 'react-router-dom';
import { RefreshCw, FileDown, ArrowUp, ArrowDown, ArrowUpDown, AlertTriangle, Smartphone, CheckCircle2, Activity, Trash2, CircleAlert } from 'lucide-react';
import { apiFetch, apiFetchWithRetry } from '../lib/apiClient.ts';
import { Button } from '../components/ui/button.tsx';
import { Input } from '../components/ui/input.tsx';
import { Badge, type BadgeProps } from '../components/ui/badge.tsx';
import { Skeleton } from '../components/ui/skeleton.tsx';
import { EmptyState } from '../components/ui/empty-state.tsx';
import { ConfirmImpactDialog, type ConfirmImpactCopy } from '../components/ConfirmImpactDialog.tsx';
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
        // Tailwind's preflight resets `text-transform: none` on <button>, which would otherwise
        // override the `uppercase` class inherited from the parent <th> (see table.tsx) — reassert
        // it here so sortable headers render in the same ALL CAPS style as static ones.
        className="inline-flex items-center gap-1 uppercase hover:text-foreground focus-visible:outline-none"
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
  const errorId = useId();

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
    <div className="relative inline-flex items-center">
      <Input
        type="text"
        maxLength={6}
        placeholder="Passcode"
        value={passcode}
        onChange={e => { setPasscode(e.target.value.toUpperCase()); setError(null); }}
        disabled={busy}
        aria-invalid={error ? true : undefined}
        aria-describedby={error ? errorId : undefined}
        className={cn(
          'h-8 w-28 text-center font-mono text-xs tracking-widest',
          error && 'border-destructive pr-6 focus-visible:ring-destructive/40',
        )}
      />
      {error && (
        <span title={error} className="absolute right-1.5 flex text-destructive">
          <CircleAlert className="h-3.5 w-3.5" aria-hidden="true" />
          <span id={errorId} className="sr-only">{error}</span>
        </span>
      )}
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
  const [pendingBulkAssign, setPendingBulkAssign] = useState(false);
  const timerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  // Consecutive failed /api/sessions polls, used to back off the poll interval (see
  // scheduleNextPoll); reset to 0 the moment a poll succeeds again.
  const consecutiveFailuresRef = useRef(0);

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
        consecutiveFailuresRef.current = 0;
        return fetched;
      }
      consecutiveFailuresRef.current += 1;
    } catch { consecutiveFailuresRef.current += 1; /* retain previous */ }
    finally { setLoading(false); }
    return data ?? [];
  }, []);

  const scheduleNextPoll = useCallback((data: Session[]) => {
    if (timerRef.current) clearTimeout(timerRef.current);
    const hasHot = data.some(s => s.state === 'SessionStarted' || s.state === 'SessionInProgress');
    const baseInterval = hasHot ? 5_000 : 30_000;
    // Back off exponentially after consecutive failed polls (transient network blips, upstream
    // 5xx, etc.) so we don't hammer a struggling backend — capped at 5 minutes, and reset to the
    // normal cadence as soon as a poll succeeds again.
    const failures = consecutiveFailuresRef.current;
    const interval = failures > 0 ? Math.min(baseInterval * 2 ** failures, 300_000) : baseInterval;
    timerRef.current = setTimeout(async () => {
      const next = await fetchSessions(data);
      scheduleNextPoll(next);
    }, interval);
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

  const [removingSessionId, setRemovingSessionId] = useState<string | null>(null);

  const handleRemoveCoupledSession = async (sessionId: string) => {
    if (!confirm('Remove this coupled device? Use this if the device or VM was rebooted or aborted before imaging started.')) return;
    setRemovingSessionId(sessionId);
    try {
      const res = await apiFetch(`/api/sessions/${sessionId}`, { method: 'DELETE', credentials: 'include' });
      if (res.ok || res.status === 204) {
        notify({ status: 'success', title: 'Coupled device removed.' });
        handleRefresh();
      } else if (res.status === 409) {
        notify({ status: 'error', title: 'This device is no longer in a removable state. Refreshing…' });
        handleRefresh();
      } else {
        notify({ status: 'error', title: 'Failed to remove device. Please try again.' });
      }
    } catch {
      notify({ status: 'error', title: 'Network error. Please try again.' });
    } finally {
      setRemovingSessionId(null);
    }
  };

  const handleStartImaging = async () => {
    if (!hasOsImages) {
      notify({ status: 'error', title: 'Upload an OS image before starting imaging.' });
      return;
    }
    if (!selectedImageId || coupled.length === 0 || startingImages) return;
    setPendingBulkAssign(false);
    setStartingImages(true);
    try {
      const res = await apiFetch('/api/sessions/bulk-assign', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        credentials: 'include',
        body: JSON.stringify({ sessionIds: coupled.map(s => s.sessionId), osImageId: selectedImageId }),
      });
      if (res.ok) {
        const data = await res.json() as { assigned: number; skipped: number; assignedIds: string[]; skippedIds: string[] };
        const assignedTitle = `Imaging started for ${data.assigned} device${data.assigned !== 1 ? 's' : ''}.`;
        if (data.skipped > 0) {
          const skippedSerials = coupled
            .filter(s => data.skippedIds.includes(s.sessionId))
            .map(s => s.deviceSerialNumber);
          notify({
            status: data.assigned > 0 ? 'info' : 'error',
            title: assignedTitle,
            description: skippedSerials.length > 0
              ? `${data.skipped} skipped (no longer coupled or already assigned): ${skippedSerials.join(', ')}.`
              : `${data.skipped} device${data.skipped !== 1 ? 's' : ''} skipped (no longer coupled or already assigned).`,
          });
        } else {
          notify({ status: 'success', title: assignedTitle });
        }
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

  const selectedImageLabel = images.find(i => i.imageId === selectedImageId);

  const bulkAssignCopy: ConfirmImpactCopy = {
    confirmTitle: `Start imaging on ${coupled.length} device${coupled.length !== 1 ? 's' : ''}?`,
    impact: selectedImageLabel
      ? `This assigns "${selectedImageLabel.name} ${selectedImageLabel.version}" to all ${coupled.length} coupled device${coupled.length !== 1 ? 's' : ''} and immediately begins imaging. This cannot be undone.`
      : `This assigns the selected OS image to all ${coupled.length} coupled device${coupled.length !== 1 ? 's' : ''} and immediately begins imaging. This cannot be undone.`,
    confirmLabel: 'Start Imaging',
    destructive: false,
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
              <span className="inline-flex min-w-5 select-none cursor-default items-center justify-center rounded-full bg-muted px-1.5 py-0.5 text-xs font-semibold text-muted-foreground">
                {available.length}
              </span>
            </div>
            <Table className="table-fixed">
              <TableHeader>
                <TableRow className="hover:bg-transparent">
                  <SortableHead label="Serial" sortKey="serial" sort={availableSort} onSort={toggleAvailableSort} className="w-[22%]" />
                  <SortableHead label="Device" sortKey="device" sort={availableSort} onSort={toggleAvailableSort} className="w-[32%]" />
                  <SortableHead label="State" sortKey="state" sort={availableSort} onSort={toggleAvailableSort} className="w-[14%]" />
                  <TableHead className="w-[16%]">Passcode</TableHead>
                  <SortableHead label="Registered" sortKey="registered" sort={availableSort} onSort={toggleAvailableSort} className="w-[16%]" />
                </TableRow>
              </TableHeader>
              <TableBody>
                {loading && sessions.length === 0 ? (
                  Array.from({ length: 3 }).map((_, i) => (
                    <TableRow key={`av-skeleton-${i}`} className="hover:bg-transparent">
                      <TableCell><Skeleton className="h-4 w-24" /></TableCell>
                      <TableCell><Skeleton className="h-4 w-32" /></TableCell>
                      <TableCell><Skeleton className="h-5 w-20 rounded-full" /></TableCell>
                      <TableCell><Skeleton className="h-8 w-28" /></TableCell>
                      <TableCell><Skeleton className="h-4 w-24" /></TableCell>
                    </TableRow>
                  ))
                ) : available.length === 0 ? (
                  <TableRow className="hover:bg-transparent">
                    <TableCell colSpan={5} className="p-0">
                      <EmptyState
                        icon={Smartphone}
                        title="No devices waiting to be coupled"
                        description="Newly registered devices will appear here automatically."
                      />
                    </TableCell>
                  </TableRow>
                ) : available.map(s => (
                  <TableRow key={s.sessionId}>
                    <TableCell className="font-mono text-xs">{s.deviceSerialNumber}</TableCell>
                    <TableCell>{s.deviceManufacturer} {s.deviceModel}</TableCell>
                    <TableCell><Badge variant={stateBadgeVariant(s.state)} dot>{stateLabel(s.state)}</Badge></TableCell>
                    <TableCell>
                      <PasscodeCouplingCell onCoupled={handleRefresh} />
                    </TableCell>
                    <TableCell className="text-xs text-muted-foreground">{formatRegistered(s.createdAt)}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </div>

          <div className="rounded-md border border-border overflow-hidden">
            <div className="flex flex-wrap items-center justify-between gap-3 border-b border-border px-4 py-3">
              <div className="flex items-center gap-2">
                <h3 className="text-sm font-semibold">Coupled Devices</h3>
                <span className="inline-flex min-w-5 select-none cursor-default items-center justify-center rounded-full bg-primary/15 px-1.5 py-0.5 text-xs font-semibold text-primary">
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
                  onClick={() => setPendingBulkAssign(true)}
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
                  <SortableHead label="Serial" sortKey="serial" sort={coupledSort} onSort={toggleCoupledSort} className="w-[26%]" />
                  <SortableHead label="Device" sortKey="device" sort={coupledSort} onSort={toggleCoupledSort} className="w-[36%]" />
                  <SortableHead label="Registered" sortKey="registered" sort={coupledSort} onSort={toggleCoupledSort} className="w-[26%]" />
                  {/* Fixed pixel width so the remove-icon button never gets squeezed as the table shrinks.
                      The other columns above intentionally leave headroom (don't sum to 100%) so this
                      fixed column doesn't push the table wider than its container. */}
                  <TableHead className="w-[64px]">Actions</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {coupled.length === 0 ? (
                  <TableRow className="hover:bg-transparent">
                    <TableCell colSpan={4} className="p-0">
                      <EmptyState
                        icon={CheckCircle2}
                        title="No devices ready for imaging"
                        description="Couple a device above using its passcode to see it here."
                      />
                    </TableCell>
                  </TableRow>
                ) : coupled.map(s => (
                  <TableRow key={s.sessionId}>
                    <TableCell className="font-mono text-xs">{s.deviceSerialNumber}</TableCell>
                    <TableCell>{s.deviceManufacturer} {s.deviceModel}</TableCell>
                    <TableCell className="text-xs text-muted-foreground">{formatRegistered(s.createdAt)}</TableCell>
                    <TableCell>
                      <Button
                        variant="ghost"
                        size="icon"
                        title="Remove this coupled device (e.g. if the device or VM was rebooted/aborted)"
                        disabled={removingSessionId === s.sessionId}
                        className="text-muted-foreground hover:bg-destructive/10 hover:text-destructive"
                        onClick={() => void handleRemoveCoupledSession(s.sessionId)}
                      >
                        <Trash2 className="h-4 w-4" />
                      </Button>
                    </TableCell>
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
                <SortableHead label="Serial" sortKey="serial" sort={monitorSort} onSort={toggleMonitorSort} className="w-[18%]" />
                <SortableHead label="Device" sortKey="device" sort={monitorSort} onSort={toggleMonitorSort} className="w-[24%]" />
                {/* State and Actions are fixed pixel widths (not %) so they never shrink below the
                    space their badge/button needs — the other columns share whatever space remains. */}
                <SortableHead label="State" sortKey="state" sort={monitorSort} onSort={toggleMonitorSort} className="w-[140px]" />
                <SortableHead label="Progress" sortKey="progress" sort={monitorSort} onSort={toggleMonitorSort} className="w-[21%]" />
                <SortableHead label="Step" sortKey="step" sort={monitorSort} onSort={toggleMonitorSort} className="w-[18%]" />
                <TableHead className="w-[150px]">Actions</TableHead>
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
                    <TableCell><Skeleton className="h-8 w-24" /></TableCell>
                  </TableRow>
                ))
              ) : monitor.length === 0 ? (
                <TableRow className="hover:bg-transparent">
                  <TableCell colSpan={6} className="p-0">
                    <EmptyState
                      icon={Activity}
                      title="No imaging activity yet"
                      description="Sessions will appear here once imaging starts."
                    />
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
                  <TableCell>
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

      {pendingBulkAssign && (
        <ConfirmImpactDialog
          copy={bulkAssignCopy}
          busy={startingImages}
          onCancel={() => setPendingBulkAssign(false)}
          onConfirm={() => { void handleStartImaging(); }}
          titleId="bulk-assign-confirm-title"
        />
      )}
    </div>
  );
}
