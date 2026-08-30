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
import { useUserPreferences } from '../context/userPreferencesContext.tsx';
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
  locationId: string | null;
  locationName: string | null;
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
}

// States for which the Client may have uploaded a diagnostic log on failure. These get their own
// tab rather than sitting alongside healthy sessions in Monitor.
const FAILED_STATES = new Set(['SessionFailed', 'SessionNotAuthorized']);

type DeviceView = 'pending' | 'monitor' | 'failed';

// Available: newly-registered sessions awaiting a technician to enter the device's passcode.
const AVAILABLE_STATES = new Set(['SessionInit', 'SessionAllowed']);
// Coupled: passcode has been matched — eligible for OS image selection.
const COUPLED_STATES = new Set(['SessionAssigned']);
// Monitor: devices that were coupled and then had imaging started, through to completion.
const MONITOR_STATES = new Set(['SessionStarted', 'SessionInProgress', 'SessionCompleted']);

function deriveCounts(sessions: Session[]) {
  return {
    pending: sessions.filter(s => AVAILABLE_STATES.has(s.state) || COUPLED_STATES.has(s.state)).length,
    monitor: sessions.filter(s => MONITOR_STATES.has(s.state)).length,
    failed:  sessions.filter(s => FAILED_STATES.has(s.state)).length,
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

/**
 * Label for the OS image picker. A native <select> popup sizes itself to its longest option and
 * ignores the control's width, so an image whose catalog name is the raw uploaded file name (the
 * default) could otherwise open a dropdown running off the side of the page. The picker sits early
 * in its row (see the Coupled Devices toolbar) precisely so there's ample room for the popup to
 * expand into, so this only needs to catch pathological outliers rather than routinely truncate.
 * The operator-authored version leads and is never truncated, since that is what identifies the
 * image and it is unique across the catalog.
 */
const MAX_IMAGE_OPTION_CHARS = 100;

function imageOptionLabel(image: OsImage): string {
  const name = image.name.replace(/\.(wim|esd|iso)$/i, '').trim();
  const label = name && name !== image.version ? `${image.version} (${name})` : image.version;
  return label.length > MAX_IMAGE_OPTION_CHARS
    ? `${label.slice(0, MAX_IMAGE_OPTION_CHARS - 1)}\u2026`
    : label;
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
  const { preferredLocationId, preferredLocationName } = useUserPreferences();
  const [sessions, setSessions]         = useState<Session[]>([]);
  const [view, setView]                 = useState<DeviceView>('pending');
  // Hard filter to the signed-in user's preferred location (FR: technicians at a 50+ site
  // customer should not see, and cannot accidentally couple, devices registered elsewhere).
  // Defaults to filtered ON whenever a preference is set; always OFF (and hidden) otherwise.
  const [showAllLocations, setShowAllLocations] = useState(false);
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

  const [availableSort, toggleAvailableSort] = useSort<'serial' | 'device' | 'location' | 'state' | 'registered'>('registered');
  const [coupledSort, toggleCoupledSort]     = useSort<'serial' | 'device' | 'location' | 'registered'>('registered');
  const [monitorSort, toggleMonitorSort]     = useSort<'serial' | 'device' | 'location' | 'state' | 'progress' | 'step'>('state');
  const [failedSort, toggleFailedSort]       = useSort<'serial' | 'device' | 'location' | 'state' | 'step' | 'registered'>('registered');

  // Uploaded diagnostic logs per failed session, looked up once the Failed tab is opened. A
  // session only has a log if the Client managed a best-effort upload before reboot, so the
  // download button stays disabled until we know there is actually something to download.
  const [sessionLogs, setSessionLogs] = useState<Record<string, SessionLogEntry[]>>({});
  const logLookupsRef = useRef<Set<string>>(new Set());

  useEffect(() => {
    void (async () => {
      try {
        // GET /api/images already returns only active catalog entries (OsImageRepository
        // .ListActiveAsync), so nothing is filtered client-side here.
        const res = await apiFetch('/api/images', { credentials: 'include' });
        if (res.ok) {
          setImages(await res.json() as OsImage[]);
        }
      } catch { /* leave list empty */ }
      finally { setImagesLoaded(true); }
    })();
  }, []);

  const handleDownloadLog = useCallback(async (sessionId: string) => {
    const entries = sessionLogs[sessionId];
    if (!entries || entries.length === 0) return;

    setDownloadingLog(sessionId);
    try {
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
  }, [notify, sessionLogs]);

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

  const locationFiltered = useMemo(() => (
    !preferredLocationId || showAllLocations
      ? sessions
      : sessions.filter(s => s.locationId === preferredLocationId)
  ), [sessions, preferredLocationId, showAllLocations]);
  const counts    = deriveCounts(locationFiltered);
  const available = useMemo(() => sortRows(
    locationFiltered.filter(s => AVAILABLE_STATES.has(s.state)),
    availableSort,
    {
      serial:     (s: Session) => s.deviceSerialNumber,
      device:     (s: Session) => `${s.deviceManufacturer} ${s.deviceModel}`,
      location:   (s: Session) => s.locationName ?? '',
      state:      (s: Session) => stateLabel(s.state),
      registered: (s: Session) => new Date(s.createdAt).getTime(),
    },
  ), [locationFiltered, availableSort]);
  const coupled = useMemo(() => sortRows(
    locationFiltered.filter(s => COUPLED_STATES.has(s.state)),
    coupledSort,
    {
      serial:     (s: Session) => s.deviceSerialNumber,
      device:     (s: Session) => `${s.deviceManufacturer} ${s.deviceModel}`,
      location:   (s: Session) => s.locationName ?? '',
      registered: (s: Session) => new Date(s.createdAt).getTime(),
    },
  ), [locationFiltered, coupledSort]);
  const monitor = useMemo(() => sortRows(
    locationFiltered.filter(s => MONITOR_STATES.has(s.state)),
    monitorSort,
    {
      serial:   (s: Session) => s.deviceSerialNumber,
      device:   (s: Session) => `${s.deviceManufacturer} ${s.deviceModel}`,
      location: (s: Session) => s.locationName ?? '',
      state:    (s: Session) => stateLabel(s.state),
      progress: (s: Session) => s.overallProgressPercent,
      step:     (s: Session) => s.currentStep ?? '',
    },
  ), [locationFiltered, monitorSort]);
  const failed = useMemo(() => sortRows(
    locationFiltered.filter(s => FAILED_STATES.has(s.state)),
    failedSort,
    {
      serial:     (s: Session) => s.deviceSerialNumber,
      device:     (s: Session) => `${s.deviceManufacturer} ${s.deviceModel}`,
      location:   (s: Session) => s.locationName ?? '',
      state:      (s: Session) => stateLabel(s.state),
      step:       (s: Session) => s.currentStep ?? '',
      registered: (s: Session) => new Date(s.createdAt).getTime(),
    },
  ), [locationFiltered, failedSort]);

  // Look up log availability only for the failed sessions currently on screen, and only once per
  // session, so opening the tab costs one request per failed device rather than one per poll.
  useEffect(() => {
    if (view !== 'failed') return;
    const pending = failed.filter(s => !logLookupsRef.current.has(s.sessionId));
    if (pending.length === 0) return;

    pending.forEach(s => logLookupsRef.current.add(s.sessionId));
    void (async () => {
      const results = await Promise.all(pending.map(async s => {
        try {
          const res = await apiFetch(`/api/sessions/${s.sessionId}/logs`, { credentials: 'include' });
          return [s.sessionId, res.ok ? await res.json() as SessionLogEntry[] : []] as const;
        } catch {
          // Treat an unreachable lookup as "no log", which leaves the button disabled rather
          // than offering a download that would fail on click.
          return [s.sessionId, [] as SessionLogEntry[]] as const;
        }
      }));
      setSessionLogs(prev => ({ ...prev, ...Object.fromEntries(results) }));
    })();
  }, [view, failed]);

  const handleRefresh = () => {
    // A log is uploaded best-effort just after the failure, so a session that had none a moment
    // ago may have one now; drop those cached "no log" answers and let them be looked up again.
    logLookupsRef.current = new Set(
      [...logLookupsRef.current].filter(id => (sessionLogs[id]?.length ?? 0) > 0));
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
      ? `This assigns "${selectedImageLabel.version}" to all ${coupled.length} coupled device${coupled.length !== 1 ? 's' : ''} and immediately begins imaging. This cannot be undone.`
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
            { key: 'failed',  label: 'Failed',  count: counts.failed },
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
        <div className="flex items-center gap-3">
          {preferredLocationId && (
            <label className="flex items-center gap-1.5 text-xs text-muted-foreground">
              <input
                type="checkbox"
                checked={showAllLocations}
                onChange={e => setShowAllLocations(e.target.checked)}
                className="h-3.5 w-3.5 rounded border-input"
              />
              Show all locations
              {!showAllLocations && preferredLocationName && (
                <span className="font-medium text-foreground">(filtered to {preferredLocationName})</span>
              )}
            </label>
          )}
          <Button variant="outline" size="sm" onClick={handleRefresh}>
            <RefreshCw className={loading ? 'animate-spin' : ''} /> Refresh
          </Button>
        </div>
      </div>

      <p className="text-sm text-muted-foreground">
        {view === 'pending'
          ? 'Enter a device\u2019s passcode to couple it, then assign an OS image to start imaging.'
          : view === 'monitor'
            ? 'Deployment progress and status for devices that have started imaging, updated in real time.'
            : 'Devices whose imaging failed or that were never authorized. Download the diagnostic log where the Client managed to upload one.'}
      </p>

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
                  <SortableHead label="Serial" sortKey="serial" sort={availableSort} onSort={toggleAvailableSort} className="w-[18%]" />
                  <SortableHead label="Device" sortKey="device" sort={availableSort} onSort={toggleAvailableSort} className="w-[24%]" />
                  <SortableHead label="Location" sortKey="location" sort={availableSort} onSort={toggleAvailableSort} className="w-[16%]" />
                  <SortableHead label="State" sortKey="state" sort={availableSort} onSort={toggleAvailableSort} className="w-[12%]" />
                  <TableHead className="w-[13%]">Passcode</TableHead>
                  <SortableHead label="Registered" sortKey="registered" sort={availableSort} onSort={toggleAvailableSort} className="w-[13%]" />
                </TableRow>
              </TableHeader>
              <TableBody>
                {loading && sessions.length === 0 ? (
                  Array.from({ length: 3 }).map((_, i) => (
                    <TableRow key={`av-skeleton-${i}`} className="hover:bg-transparent">
                      <TableCell><Skeleton className="h-4 w-24" /></TableCell>
                      <TableCell><Skeleton className="h-4 w-32" /></TableCell>
                      <TableCell><Skeleton className="h-4 w-20" /></TableCell>
                      <TableCell><Skeleton className="h-5 w-20 rounded-full" /></TableCell>
                      <TableCell><Skeleton className="h-8 w-28" /></TableCell>
                      <TableCell><Skeleton className="h-4 w-24" /></TableCell>
                    </TableRow>
                  ))
                ) : available.length === 0 ? (
                  <TableRow className="hover:bg-transparent">
                    <TableCell colSpan={6} className="p-0">
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
                    <TableCell className="text-xs text-muted-foreground">{s.locationName ?? '\u2014'}</TableCell>
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
            <div className="flex flex-wrap items-center gap-3 border-b border-border px-4 py-3">
              <div className="flex shrink-0 items-center gap-2">
                <h3 className="text-sm font-semibold">Coupled Devices</h3>
                <span className="inline-flex min-w-5 select-none cursor-default items-center justify-center rounded-full bg-primary/15 px-1.5 py-0.5 text-xs font-semibold text-primary">
                  {coupled.length}
                </span>
              </div>
              {/* Placed here (rather than beside the button) so it gets the bulk of the row's
                  width and, just as importantly, sits well clear of the right edge of the page —
                  the native popup opens flush with the control and ignores its own width, so
                  giving it room on this side lets long catalog names show in full (see
                  imageOptionLabel). */}
              <select
                value={selectedImageId ?? ''}
                onChange={e => setSelectedImageId(e.target.value || null)}
                disabled={coupled.length === 0 || images.length === 0}
                title={!hasOsImages ? 'Upload an OS image before assigning one to coupled devices.' : undefined}
                className="h-8 min-w-0 flex-1 truncate rounded-md border border-input bg-background px-2 text-sm shadow-sm disabled:cursor-not-allowed disabled:opacity-50"
              >
                <option value="">{hasOsImages ? 'Select OS image…' : 'No OS images uploaded'}</option>
                {images.map(img => (
                  <option key={img.imageId} value={img.imageId}>{imageOptionLabel(img)}</option>
                ))}
              </select>
              <Button
                size="sm"
                className="shrink-0"
                onClick={() => setPendingBulkAssign(true)}
                disabled={!selectedImageId || coupled.length === 0 || startingImages}
                title={!hasOsImages ? 'Upload an OS image before starting imaging.' : undefined}
              >
                {startingImages ? 'Starting…' : `Start Imaging (${coupled.length})`}
              </Button>
            </div>
            <Table className="table-fixed">
              <TableHeader>
                <TableRow className="hover:bg-transparent">
                  <SortableHead label="Serial" sortKey="serial" sort={coupledSort} onSort={toggleCoupledSort} className="w-[22%]" />
                  <SortableHead label="Device" sortKey="device" sort={coupledSort} onSort={toggleCoupledSort} className="w-[28%]" />
                  <SortableHead label="Location" sortKey="location" sort={coupledSort} onSort={toggleCoupledSort} className="w-[18%]" />
                  <SortableHead label="Registered" sortKey="registered" sort={coupledSort} onSort={toggleCoupledSort} className="w-[20%]" />
                  {/* Fixed pixel width so the remove-icon button never gets squeezed as the table shrinks.
                      The other columns above intentionally leave headroom (don't sum to 100%) so this
                      fixed column doesn't push the table wider than its container. */}
                  <TableHead className="w-[64px]">Actions</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {coupled.length === 0 ? (
                  <TableRow className="hover:bg-transparent">
                    <TableCell colSpan={5} className="p-0">
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
                    <TableCell className="text-xs text-muted-foreground">{s.locationName ?? '\u2014'}</TableCell>
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
      ) : view === 'monitor' ? (
        <div className="rounded-md border border-border overflow-hidden">
          <Table className="table-fixed">
            <TableHeader>
              <TableRow className="hover:bg-transparent">
                <SortableHead label="Serial" sortKey="serial" sort={monitorSort} onSort={toggleMonitorSort} className="w-[16%]" />
                <SortableHead label="Device" sortKey="device" sort={monitorSort} onSort={toggleMonitorSort} className="w-[24%]" />
                <SortableHead label="Location" sortKey="location" sort={monitorSort} onSort={toggleMonitorSort} className="w-[16%]" />
                {/* State is a fixed pixel width (not %) so it never shrinks below the space its
                    badge needs — the other columns share whatever space remains. */}
                <SortableHead label="State" sortKey="state" sort={monitorSort} onSort={toggleMonitorSort} className="w-[140px]" />
                <SortableHead label="Progress" sortKey="progress" sort={monitorSort} onSort={toggleMonitorSort} className="w-[20%]" />
                <SortableHead label="Step" sortKey="step" sort={monitorSort} onSort={toggleMonitorSort} className="w-[16%]" />
              </TableRow>
            </TableHeader>
            <TableBody>
              {loading && sessions.length === 0 ? (
                Array.from({ length: 5 }).map((_, i) => (
                  <TableRow key={`mon-skeleton-${i}`} className="hover:bg-transparent">
                    <TableCell><Skeleton className="h-4 w-24" /></TableCell>
                    <TableCell><Skeleton className="h-4 w-32" /></TableCell>
                    <TableCell><Skeleton className="h-4 w-20" /></TableCell>
                    <TableCell><Skeleton className="h-5 w-20 rounded-full" /></TableCell>
                    <TableCell><Skeleton className="h-4 w-28" /></TableCell>
                    <TableCell><Skeleton className="h-4 w-24" /></TableCell>
                  </TableRow>
                ))
              ) : monitor.length === 0 ? (
                <TableRow className="hover:bg-transparent">
                  <TableCell colSpan={6} className="p-0">
                    <EmptyState
                      icon={Activity}
                      title="No imaging activity yet"
                      description="Devices appear here once you couple them and start imaging."
                    />
                  </TableCell>
                </TableRow>
              ) : monitor.map(s => (
                <TableRow key={s.sessionId}>
                  <TableCell className="font-mono text-xs">{s.deviceSerialNumber}</TableCell>
                  <TableCell>{s.deviceManufacturer} {s.deviceModel}</TableCell>
                  <TableCell className="text-xs text-muted-foreground">{s.locationName ?? '\u2014'}</TableCell>
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
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </div>
      ) : (
        <div className="rounded-md border border-border overflow-hidden">
          <Table className="table-fixed">
            <TableHeader>
              <TableRow className="hover:bg-transparent">
                <SortableHead label="Serial" sortKey="serial" sort={failedSort} onSort={toggleFailedSort} className="w-[16%]" />
                <SortableHead label="Device" sortKey="device" sort={failedSort} onSort={toggleFailedSort} className="w-[22%]" />
                <SortableHead label="Location" sortKey="location" sort={failedSort} onSort={toggleFailedSort} className="w-[15%]" />
                <SortableHead label="State" sortKey="state" sort={failedSort} onSort={toggleFailedSort} className="w-[140px]" />
                <SortableHead label="Last step" sortKey="step" sort={failedSort} onSort={toggleFailedSort} className="w-[17%]" />
                <SortableHead label="Registered" sortKey="registered" sort={failedSort} onSort={toggleFailedSort} className="w-[14%]" />
                <TableHead className="w-[150px]">Actions</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {loading && sessions.length === 0 ? (
                Array.from({ length: 3 }).map((_, i) => (
                  <TableRow key={`fail-skeleton-${i}`} className="hover:bg-transparent">
                    <TableCell><Skeleton className="h-4 w-24" /></TableCell>
                    <TableCell><Skeleton className="h-4 w-32" /></TableCell>
                    <TableCell><Skeleton className="h-4 w-20" /></TableCell>
                    <TableCell><Skeleton className="h-5 w-20 rounded-full" /></TableCell>
                    <TableCell><Skeleton className="h-4 w-24" /></TableCell>
                    <TableCell><Skeleton className="h-4 w-24" /></TableCell>
                    <TableCell><Skeleton className="h-8 w-32" /></TableCell>
                  </TableRow>
                ))
              ) : failed.length === 0 ? (
                <TableRow className="hover:bg-transparent">
                  <TableCell colSpan={7} className="p-0">
                    <EmptyState
                      icon={CheckCircle2}
                      title="No failed devices"
                      description="Devices that fail imaging or are denied authorization appear here."
                    />
                  </TableCell>
                </TableRow>
              ) : failed.map(s => {
                const logs = sessionLogs[s.sessionId];
                const logsUnknown = logs === undefined;
                const hasLog = !logsUnknown && logs.length > 0;
                return (
                  <TableRow key={s.sessionId}>
                    <TableCell className="font-mono text-xs">{s.deviceSerialNumber}</TableCell>
                    <TableCell>{s.deviceManufacturer} {s.deviceModel}</TableCell>
                    <TableCell className="text-xs text-muted-foreground">{s.locationName ?? '\u2014'}</TableCell>
                    <TableCell><Badge variant={stateBadgeVariant(s.state)} dot>{stateLabel(s.state)}</Badge></TableCell>
                    <TableCell className="text-xs text-muted-foreground">{s.currentStep ?? '-'}</TableCell>
                    <TableCell className="text-xs text-muted-foreground">{formatRegistered(s.createdAt)}</TableCell>
                    <TableCell>
                      <Button
                        variant="outline"
                        size="sm"
                        disabled={!hasLog || downloadingLog === s.sessionId}
                        title={
                          logsUnknown ? 'Checking for an uploaded diagnostic log…'
                          : hasLog ? 'Download the diagnostic log the Client uploaded for this device.'
                          : 'This device never uploaded a diagnostic log, so there is nothing to download.'
                        }
                        onClick={() => { void handleDownloadLog(s.sessionId); }}
                      >
                        <FileDown className={downloadingLog === s.sessionId ? 'animate-pulse' : ''} /> Download log
                      </Button>
                    </TableCell>
                  </TableRow>
                );
              })}
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
