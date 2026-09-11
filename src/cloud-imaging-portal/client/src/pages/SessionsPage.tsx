// Full Sessions page implementation, see import below
export default function SessionsPage(): React.ReactElement {
  return <SessionsPageImpl />;
}

import { useState, useEffect, useCallback, useRef, useMemo, useId, Fragment } from 'react';
import { Link } from 'react-router-dom';
import { RefreshCw, FileDown, AlertTriangle, Smartphone, CheckCircle2, Activity, Trash2, CircleAlert, ChevronRight, ChevronDown } from 'lucide-react';
import { apiFetch, apiFetchWithRetry } from '../lib/apiClient.ts';
import { Button } from '../components/ui/button.tsx';
import { Input } from '../components/ui/input.tsx';
import { Select } from '../components/ui/select.tsx';
import { Badge, type BadgeProps } from '../components/ui/badge.tsx';
import { Skeleton } from '../components/ui/skeleton.tsx';
import { EmptyState } from '../components/ui/empty-state.tsx';
import { CopyableId } from '../components/ui/copyable-id.tsx';
import { RelativeTime } from '../components/ui/relative-time.tsx';
import { Tooltip } from '../components/ui/tooltip.tsx';
import { ConfirmImpactDialog, type ConfirmImpactCopy } from '../components/ConfirmImpactDialog.tsx';
import { SessionDetailsPanel, type SessionHardware } from '../components/SessionDetailsPanel.tsx';
import { SessionProgressDetails } from '../components/SessionProgressDetails.tsx';
import { formatOsImageInventoryName } from '../lib/imageInventoryFormatting.ts';
import { progressStepLabel, type ImagingStepDetails } from '../lib/imagingProgress.ts';
import { cn, formatDateTime } from '../lib/utils.ts';
import { useSort, sortRows } from '../lib/tableSort.ts';
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
import { SortableHead } from '../components/ui/sortable-head.tsx';

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
  steps?: ImagingStepDetails[];
  createdAt: string;
  // Already present on the Operator API's session summary; the portal simply was not reading
  // them. Optional so a cached/older response cannot blank the table.
  preFlightAuthorizationResult?: string | null;
  assignedOsImageId?: string | null;
  lastHeartbeatAt?: string | null;
  terminalAt?: string | null;
  macAddress?: string | null;
  hardware?: SessionHardware | null;
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

type DeviceView = 'pending' | 'monitor' | 'success' | 'failed';

// Available: newly-registered sessions awaiting a technician to enter the device's passcode.
const AVAILABLE_STATES = new Set(['SessionInit', 'SessionAllowed']);
// Coupled: passcode has been matched — eligible for OS image selection.
const COUPLED_STATES = new Set(['SessionAssigned']);
// Monitor: devices with active imaging work. Completed devices move to Success.
const MONITOR_STATES = new Set(['SessionStarted', 'SessionInProgress']);
const SUCCESS_STATES = new Set(['SessionCompleted']);

function deriveCounts(sessions: Session[]) {
  return {
    pending: sessions.filter(s => AVAILABLE_STATES.has(s.state) || COUPLED_STATES.has(s.state)).length,
    monitor: sessions.filter(s => MONITOR_STATES.has(s.state)).length,
    success: sessions.filter(s => SUCCESS_STATES.has(s.state)).length,
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

/**
 * Label for the OS image picker. Combines the catalog name and optional version so technicians can
 * distinguish related images. The truncation guard stays as a backstop for pathologically long
 * metadata; `Select` sizes its list to the control and ellipsizes anything longer.
 */
const MAX_IMAGE_OPTION_CHARS = 100;

function imageOptionLabel(image: OsImage): string {
  const label = formatOsImageInventoryName(image.name, image.version);
  return label.length > MAX_IMAGE_OPTION_CHARS
    ? `${label.slice(0, MAX_IMAGE_OPTION_CHARS - 1)}\u2026`
    : label;
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
          <CircleAlert className="h-4 w-4" aria-hidden="true" />
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
  // Rows the operator has expanded for detail, keyed by session id. Kept as a set rather than a
  // single id so several devices can be compared side by side, and held here rather than inside a
  // row component so a poll that re-renders the table cannot silently collapse them.
  const [expandedIds, setExpandedIds] = useState<ReadonlySet<string>>(() => new Set());
  const detailsId = useId();
  const timerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  // Consecutive failed /api/sessions polls, used to back off the poll interval (see
  // scheduleNextPoll); reset to 0 the moment a poll succeeds again.
  const consecutiveFailuresRef = useRef(0);

  // Imaging (and, by extension, the Start Imaging action) is impossible until at least one
  // active OS image has been uploaded — surfaced via a persistent banner rather than a toast
  // so technicians can't miss it while coupling devices ahead of an image being ready.
  const hasOsImages = images.length > 0;

  const isExpanded = useCallback((sessionId: string) => expandedIds.has(sessionId), [expandedIds]);
  const toggleExpanded = useCallback((sessionId: string) => {
    setExpandedIds(prev => {
      const next = new Set(prev);
      if (!next.delete(sessionId)) next.add(sessionId);
      return next;
    });
  }, []);

  const [availableSort, toggleAvailableSort] = useSort<'serial' | 'device' | 'location' | 'state' | 'registered'>({ key: 'registered', dir: 'asc' });
  const [coupledSort, toggleCoupledSort]     = useSort<'serial' | 'device' | 'location' | 'registered'>({ key: 'registered', dir: 'asc' });
  const [monitorSort, toggleMonitorSort]     = useSort<'serial' | 'device' | 'location' | 'registered' | 'state' | 'progress' | 'step'>({ key: 'registered', dir: 'desc' });
  const [successSort, toggleSuccessSort]     = useSort<'serial' | 'device' | 'location' | 'finished'>({ key: 'finished', dir: 'desc' });
  const [failedSort, toggleFailedSort]       = useSort<'serial' | 'device' | 'location' | 'state' | 'step' | 'registered'>({ key: 'registered', dir: 'asc' });

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
      serial:     (s: Session) => s.deviceSerialNumber,
      device:     (s: Session) => `${s.deviceManufacturer} ${s.deviceModel}`,
      location:   (s: Session) => s.locationName ?? '',
      registered: (s: Session) => new Date(s.createdAt).getTime(),
      state:      (s: Session) => stateLabel(s.state),
      progress:   (s: Session) => s.overallProgressPercent,
      step:       (s: Session) => s.currentStep ?? '',
    },
  ), [locationFiltered, monitorSort]);
  const success = useMemo(() => sortRows(
    locationFiltered.filter(s => SUCCESS_STATES.has(s.state)),
    successSort,
    {
      serial:   (s: Session) => s.deviceSerialNumber,
      device:   (s: Session) => `${s.deviceManufacturer} ${s.deviceModel}`,
      location: (s: Session) => s.locationName ?? '',
      finished: (s: Session) => s.terminalAt ? new Date(s.terminalAt).getTime() : 0,
    },
  ), [locationFiltered, successSort]);
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
            { key: 'success', label: 'Success', count: counts.success },
            { key: 'failed',  label: 'Failed',  count: counts.failed },
          ] as const).map((tab) => {
            const isActive = tab.key === view;
            return (
              <button
                key={tab.key}
                onClick={() => setView(tab.key)}
                className={cn(
                  'inline-flex items-center gap-2 rounded-md px-3 py-2 text-sm font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring',
                  isActive
                    ? 'bg-background text-foreground shadow-sm'
                    : 'text-muted-foreground hover:text-foreground',
                )}
              >
                {tab.label}
                <span
                  className={cn(
                    'inline-flex min-w-5 items-center justify-center rounded-full px-2 py-0.5 text-xs font-semibold',
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
            <label className="flex items-center gap-2 text-xs text-muted-foreground">
              <input
                type="checkbox"
                checked={showAllLocations}
                onChange={e => setShowAllLocations(e.target.checked)}
                className="h-4 w-4 rounded border-input align-middle accent-primary"
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
            ? 'Deployment progress and status for devices currently imaging, updated in real time.'
            : view === 'success'
              ? 'Devices that completed imaging successfully.'
              : 'Devices whose imaging failed or that were never authorized. Download the diagnostic log where the Client managed to upload one.'}
      </p>

      {view === 'pending' ? (
        <div className="space-y-5">
          {/* text-sm, not text-xs: 12px is reserved for badges/metadata, and this is blocking
              page guidance. It also makes the icon's mt-0.5 correct — that offset centres a 16px
              icon in text-sm's 20px line box, so against a 16px text-xs line box it read low. */}
          {imagesLoaded && !hasOsImages && (
            <div className="flex items-start gap-2 rounded-md border border-amber-500/30 bg-amber-500/10 px-3 py-2 text-sm text-amber-600 dark:text-amber-400">
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
              <span className="inline-flex min-w-5 select-none cursor-default items-center justify-center rounded-full bg-muted px-2 py-0.5 text-xs font-semibold text-muted-foreground">
                {available.length}
              </span>
            </div>
            <Table className="table-fixed">
              <TableHeader>
                <TableRow className="hover:bg-transparent">
                  {/* The remaining widths already summed to 96%, leaving exactly this much for the
                      toggle without redistributing the existing columns. */}
                  <TableHead className="w-[4%]"><span className="sr-only">Expand device details</span></TableHead>
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
                      <TableCell><Skeleton className="h-4 w-4" /></TableCell>
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
                    <TableCell colSpan={7} className="p-0">
                      <EmptyState
                        icon={Smartphone}
                        title="No devices waiting to be coupled"
                        description="Newly registered devices will appear here automatically."
                      />
                    </TableCell>
                  </TableRow>
                ) : available.map(s => (
                  <Fragment key={s.sessionId}>
                    <TableRow>
                      <TableCell>
                        <Tooltip content={isExpanded(s.sessionId) ? 'Hide details' : 'Show details'}>
                          <button
                            type="button"
                            onClick={() => toggleExpanded(s.sessionId)}
                            aria-expanded={isExpanded(s.sessionId)}
                            aria-controls={`${detailsId}-${s.sessionId}`}
                            aria-label={`${isExpanded(s.sessionId) ? 'Hide' : 'Show'} details for ${s.deviceSerialNumber}`}
                            className="rounded-md p-1 text-muted-foreground transition-colors hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background"
                          >
                            {isExpanded(s.sessionId) ? <ChevronDown size={16} /> : <ChevronRight size={16} />}
                          </button>
                        </Tooltip>
                      </TableCell>
                      <TableCell>
                        <CopyableId value={s.deviceSerialNumber} label="device serial number" className="font-mono text-sm font-medium text-foreground" />
                      </TableCell>
                      <TableCell>{s.deviceManufacturer} {s.deviceModel}</TableCell>
                      <TableCell className="text-muted-foreground">{s.locationName ?? '\u2014'}</TableCell>
                      <TableCell><Badge variant={stateBadgeVariant(s.state)} dot>{stateLabel(s.state)}</Badge></TableCell>
                      <TableCell>
                        <PasscodeCouplingCell onCoupled={handleRefresh} />
                      </TableCell>
                      <TableCell className="text-xs text-muted-foreground"><RelativeTime value={s.createdAt} /></TableCell>
                    </TableRow>
                    {isExpanded(s.sessionId) && (
                      <TableRow className="hover:bg-transparent">
                        <TableCell colSpan={7} className="border-t-0 bg-muted/20 p-0" id={`${detailsId}-${s.sessionId}`}>
                          <SessionDetailsPanel session={s} />
                        </TableCell>
                      </TableRow>
                    )}
                  </Fragment>
                ))}
              </TableBody>
            </Table>
          </div>

          <div className="rounded-md border border-border overflow-hidden">
            <div className="flex flex-wrap items-center gap-3 border-b border-border px-4 py-3">
              <div className="flex shrink-0 items-center gap-2">
                <h3 className="text-sm font-semibold">Coupled Devices</h3>
                <span className="inline-flex min-w-5 select-none cursor-default items-center justify-center rounded-full bg-primary/15 px-2 py-0.5 text-xs font-semibold text-primary">
                  {coupled.length}
                </span>
              </div>
              {/* Grow with the selected label instead of pinning the picker to a short fixed width.
                  The wrapper shrink-wraps the trigger while remaining bounded by the available row;
                  the trigger then caps exceptionally long labels at 36rem. Select's portalled list
                  copies this computed trigger width, so both surfaces expose the same amount of text. */}
              <Select
                size="sm"
                wrapperClassName="w-fit min-w-0 max-w-full"
                className="w-auto min-w-[10rem] max-w-xl"
                aria-label="OS image to assign"
                value={selectedImageId ?? ''}
                onValueChange={(value: string) => setSelectedImageId(value || null)}
                options={images.map(img => ({ value: img.imageId, label: imageOptionLabel(img) }))}
                placeholder={hasOsImages ? 'Select OS image\u2026' : 'No OS images uploaded'}
                disabled={coupled.length === 0 || images.length === 0}
                title={!hasOsImages ? 'Upload an OS image before assigning one to coupled devices.' : undefined}
              />
              <Button
                size="sm"
                className="ml-auto shrink-0"
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
                    <TableCell>
                      <CopyableId value={s.deviceSerialNumber} label="device serial number" className="font-mono text-sm font-medium text-foreground" />
                    </TableCell>
                    <TableCell>{s.deviceManufacturer} {s.deviceModel}</TableCell>
                    <TableCell className="text-muted-foreground">{s.locationName ?? '\u2014'}</TableCell>
                    <TableCell className="text-xs text-muted-foreground"><RelativeTime value={s.createdAt} /></TableCell>
                    <TableCell>
                      <Tooltip content="Remove this coupled device (e.g. if the device or VM was rebooted/aborted)">
                        <Button
                          variant="ghost"
                          size="icon"
                          aria-label={`Remove coupled device ${s.deviceSerialNumber}`}
                          disabled={removingSessionId === s.sessionId}
                          className="text-muted-foreground hover:bg-destructive/10 hover:text-destructive"
                          onClick={() => void handleRemoveCoupledSession(s.sessionId)}
                        >
                          <Trash2 className="h-4 w-4" />
                        </Button>
                      </Tooltip>
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
                {/* All eight widths are percentages that sum to exactly 100% — table-fixed sizes
                    columns as (percent + fixed px) of the table's own width, so mixing in a
                    fixed px column (State was a flat 140px) made the row wider than its
                    container on anything above ~1750px and forced a horizontal scrollbar. */}
                <TableHead className="w-[4%]"><span className="sr-only">Details</span></TableHead>
                <SortableHead label="Serial / Session" sortKey="serial" sort={monitorSort} onSort={toggleMonitorSort} className="w-[14%]" />
                <SortableHead label="Device" sortKey="device" sort={monitorSort} onSort={toggleMonitorSort} className="w-[17%]" />
                <SortableHead label="Location" sortKey="location" sort={monitorSort} onSort={toggleMonitorSort} className="w-[10%]" />
                <SortableHead label="Registered" sortKey="registered" sort={monitorSort} onSort={toggleMonitorSort} className="w-[15%]" />
                <SortableHead label="State" sortKey="state" sort={monitorSort} onSort={toggleMonitorSort} className="w-[9%]" />
                <SortableHead label="Progress" sortKey="progress" sort={monitorSort} onSort={toggleMonitorSort} className="w-[15%]" />
                <SortableHead label="Step" sortKey="step" sort={monitorSort} onSort={toggleMonitorSort} className="w-[16%]" />
              </TableRow>
            </TableHeader>
            <TableBody>
              {loading && sessions.length === 0 ? (
                Array.from({ length: 5 }).map((_, i) => (
                  <TableRow key={`mon-skeleton-${i}`} className="hover:bg-transparent">
                    <TableCell><Skeleton className="h-8 w-8" /></TableCell>
                    <TableCell><Skeleton className="h-4 w-24" /></TableCell>
                    <TableCell><Skeleton className="h-4 w-32" /></TableCell>
                    <TableCell><Skeleton className="h-4 w-20" /></TableCell>
                    <TableCell><Skeleton className="h-4 w-28" /></TableCell>
                    <TableCell><Skeleton className="h-5 w-20 rounded-full" /></TableCell>
                    <TableCell><Skeleton className="h-4 w-28" /></TableCell>
                    <TableCell><Skeleton className="h-4 w-24" /></TableCell>
                  </TableRow>
                ))
              ) : monitor.length === 0 ? (
                <TableRow className="hover:bg-transparent">
                  <TableCell colSpan={8} className="p-0">
                    <EmptyState
                      icon={Activity}
                      title="No imaging activity yet"
                      description="Devices appear here once you couple them and start imaging."
                    />
                  </TableCell>
                </TableRow>
              ) : monitor.map(s => (
                <Fragment key={s.sessionId}>
                <TableRow>
                  <TableCell>
                    <Tooltip content={isExpanded(s.sessionId) ? 'Hide progress details' : 'Show progress details'}>
                      <Button
                        variant="ghost"
                        size="icon"
                        onClick={() => toggleExpanded(s.sessionId)}
                        aria-expanded={isExpanded(s.sessionId)}
                        aria-controls={`${detailsId}-${s.sessionId}`}
                        aria-label={`${isExpanded(s.sessionId) ? 'Hide' : 'Show'} progress details for ${s.deviceSerialNumber}`}
                      >
                        {isExpanded(s.sessionId) ? <ChevronDown /> : <ChevronRight />}
                      </Button>
                    </Tooltip>
                  </TableCell>
                  <TableCell>
                    <div className="space-y-1">
                      <CopyableId value={s.deviceSerialNumber} label="device serial number" className="font-mono text-sm font-medium text-foreground" />
                      <CopyableId
                        value={s.sessionId}
                        display={s.sessionId.slice(0, 8)}
                        label="session ID"
                        className="font-mono text-xs text-muted-foreground"
                      />
                    </div>
                  </TableCell>
                  <TableCell>{s.deviceManufacturer} {s.deviceModel}</TableCell>
                  <TableCell className="text-muted-foreground">{s.locationName ?? '\u2014'}</TableCell>
                  <TableCell className="text-xs text-muted-foreground">
                    <time dateTime={s.createdAt}>{formatDateTime(s.createdAt)}</time>
                  </TableCell>
                  <TableCell><Badge variant={stateBadgeVariant(s.state)} dot>{stateLabel(s.state)}</Badge></TableCell>
                  <TableCell>
                    <div className="flex items-center gap-2">
                      <div className="h-1.5 w-24 overflow-hidden rounded-full bg-muted">
                        <div className="h-full rounded-full bg-primary transition-all" style={{ width: `${s.overallProgressPercent}%` }} />
                      </div>
                      <span className="text-xs text-muted-foreground">{s.overallProgressPercent}%</span>
                    </div>
                  </TableCell>
                  <TableCell className="text-muted-foreground">{progressStepLabel(s.currentStep)}</TableCell>
                </TableRow>
                {isExpanded(s.sessionId) && (
                  <TableRow className="hover:bg-transparent">
                    <TableCell colSpan={8} className="border-t-0 bg-muted/20 p-0" id={`${detailsId}-${s.sessionId}`}>
                      <SessionProgressDetails
                        overallPercent={s.overallProgressPercent}
                        currentStep={s.currentStep}
                        steps={s.steps}
                      />
                    </TableCell>
                  </TableRow>
                )}
                </Fragment>
              ))}
            </TableBody>
          </Table>
        </div>
      ) : view === 'success' ? (
        <div className="rounded-md border border-border overflow-hidden">
          <Table className="table-fixed">
            <TableHeader>
              <TableRow className="hover:bg-transparent">
                <TableHead className="w-[5%]"><span className="sr-only">Details</span></TableHead>
                <SortableHead label="Serial" sortKey="serial" sort={successSort} onSort={toggleSuccessSort} className="w-[15%]" />
                <SortableHead label="Device" sortKey="device" sort={successSort} onSort={toggleSuccessSort} className="w-[25%]" />
                <SortableHead label="Location" sortKey="location" sort={successSort} onSort={toggleSuccessSort} className="w-[15%]" />
                <TableHead className="w-[20%]">Progress</TableHead>
                <SortableHead label="Finished" sortKey="finished" sort={successSort} onSort={toggleSuccessSort} className="w-[20%]" />
              </TableRow>
            </TableHeader>
            <TableBody>
              {loading && sessions.length === 0 ? (
                Array.from({ length: 3 }).map((_, i) => (
                  <TableRow key={`success-skeleton-${i}`} className="hover:bg-transparent">
                    <TableCell><Skeleton className="h-8 w-8" /></TableCell>
                    <TableCell><Skeleton className="h-4 w-24" /></TableCell>
                    <TableCell><Skeleton className="h-4 w-32" /></TableCell>
                    <TableCell><Skeleton className="h-4 w-20" /></TableCell>
                    <TableCell><Skeleton className="h-4 w-28" /></TableCell>
                    <TableCell><Skeleton className="h-4 w-24" /></TableCell>
                  </TableRow>
                ))
              ) : success.length === 0 ? (
                <TableRow className="hover:bg-transparent">
                  <TableCell colSpan={6} className="p-0">
                    <EmptyState
                      icon={CheckCircle2}
                      title="No successful deployments yet"
                      description="Devices appear here after every imaging stage completes."
                    />
                  </TableCell>
                </TableRow>
              ) : success.map(s => (
                <Fragment key={s.sessionId}>
                  <TableRow>
                    <TableCell>
                      <Tooltip content={isExpanded(s.sessionId) ? 'Hide progress details' : 'Show progress details'}>
                        <Button
                          variant="ghost"
                          size="icon"
                          onClick={() => toggleExpanded(s.sessionId)}
                          aria-expanded={isExpanded(s.sessionId)}
                          aria-controls={`${detailsId}-${s.sessionId}`}
                          aria-label={`${isExpanded(s.sessionId) ? 'Hide' : 'Show'} progress details for ${s.deviceSerialNumber}`}
                        >
                          {isExpanded(s.sessionId) ? <ChevronDown /> : <ChevronRight />}
                        </Button>
                      </Tooltip>
                    </TableCell>
                    <TableCell>
                      <CopyableId value={s.deviceSerialNumber} label="device serial number" className="font-mono text-sm font-medium text-foreground" />
                    </TableCell>
                    <TableCell>{s.deviceManufacturer} {s.deviceModel}</TableCell>
                    <TableCell className="text-muted-foreground">{s.locationName ?? '\u2014'}</TableCell>
                    <TableCell>
                      <div className="flex items-center gap-2">
                        <div className="h-1.5 w-24 overflow-hidden rounded-full bg-muted">
                          <div className="h-full w-full rounded-full bg-emerald-500" />
                        </div>
                        <span className="text-xs font-semibold text-emerald-600 dark:text-emerald-400">100%</span>
                      </div>
                    </TableCell>
                    <TableCell className="text-xs text-muted-foreground">
                      {s.terminalAt ? <RelativeTime value={s.terminalAt} /> : '\u2014'}
                    </TableCell>
                  </TableRow>
                  {isExpanded(s.sessionId) && (
                    <TableRow className="hover:bg-transparent">
                      <TableCell colSpan={6} className="border-t-0 bg-muted/20 p-0" id={`${detailsId}-${s.sessionId}`}>
                        <SessionProgressDetails
                          overallPercent={s.overallProgressPercent}
                          currentStep={s.currentStep}
                          steps={s.steps}
                        />
                      </TableCell>
                    </TableRow>
                  )}
                </Fragment>
              ))}
            </TableBody>
          </Table>
        </div>
      ) : (
        <div className="rounded-md border border-border overflow-hidden">
          <Table className="table-fixed">
            <TableHeader>
              <TableRow className="hover:bg-transparent">
                {/* All eight widths are percentages that sum to exactly 100% — see the Monitor
                    table's comment above for why fixed-px columns (State/Actions were
                    140px/150px) can't be mixed in here without pushing the row past the
                    container width. */}
                <TableHead className="w-[5%]"><span className="sr-only">Details</span></TableHead>
                <SortableHead label="Serial" sortKey="serial" sort={failedSort} onSort={toggleFailedSort} className="w-[12%]" />
                <SortableHead label="Device" sortKey="device" sort={failedSort} onSort={toggleFailedSort} className="w-[17%]" />
                <SortableHead label="Location" sortKey="location" sort={failedSort} onSort={toggleFailedSort} className="w-[11%]" />
                <SortableHead label="State" sortKey="state" sort={failedSort} onSort={toggleFailedSort} className="w-[10%]" />
                <SortableHead label="Last step" sortKey="step" sort={failedSort} onSort={toggleFailedSort} className="w-[14%]" />
                <SortableHead label="Registered" sortKey="registered" sort={failedSort} onSort={toggleFailedSort} className="w-[11%]" />
                <TableHead className="w-[20%]">Actions</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {loading && sessions.length === 0 ? (
                Array.from({ length: 3 }).map((_, i) => (
                  <TableRow key={`fail-skeleton-${i}`} className="hover:bg-transparent">
                    <TableCell><Skeleton className="h-8 w-8" /></TableCell>
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
                  <TableCell colSpan={8} className="p-0">
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
                  <Fragment key={s.sessionId}>
                  <TableRow>
                    <TableCell>
                      <Tooltip content={isExpanded(s.sessionId) ? 'Hide progress details' : 'Show progress details'}>
                        <Button
                          variant="ghost"
                          size="icon"
                          onClick={() => toggleExpanded(s.sessionId)}
                          aria-expanded={isExpanded(s.sessionId)}
                          aria-controls={`${detailsId}-${s.sessionId}`}
                          aria-label={`${isExpanded(s.sessionId) ? 'Hide' : 'Show'} progress details for ${s.deviceSerialNumber}`}
                        >
                          {isExpanded(s.sessionId) ? <ChevronDown /> : <ChevronRight />}
                        </Button>
                      </Tooltip>
                    </TableCell>
                    <TableCell>
                      <CopyableId value={s.deviceSerialNumber} label="device serial number" className="font-mono text-sm font-medium text-foreground" />
                    </TableCell>
                    <TableCell>{s.deviceManufacturer} {s.deviceModel}</TableCell>
                    <TableCell className="text-muted-foreground">{s.locationName ?? '\u2014'}</TableCell>
                    <TableCell><Badge variant={stateBadgeVariant(s.state)} dot>{stateLabel(s.state)}</Badge></TableCell>
                    <TableCell className="text-muted-foreground">{progressStepLabel(s.currentStep)}</TableCell>
                    <TableCell className="text-xs text-muted-foreground">
                      <time dateTime={s.createdAt}>{formatDateTime(s.createdAt)}</time>
                    </TableCell>
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
                  {isExpanded(s.sessionId) && (
                    <TableRow className="hover:bg-transparent">
                      <TableCell colSpan={8} className="border-t-0 bg-muted/20 p-0" id={`${detailsId}-${s.sessionId}`}>
                        <SessionProgressDetails
                          overallPercent={s.overallProgressPercent}
                          currentStep={s.currentStep}
                          steps={s.steps}
                        />
                      </TableCell>
                    </TableRow>
                  )}
                  </Fragment>
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
