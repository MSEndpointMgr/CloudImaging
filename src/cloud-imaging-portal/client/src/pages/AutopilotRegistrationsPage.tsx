import { useCallback, useEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { useNavigate } from 'react-router-dom';
import { AlertTriangle, CheckCircle2, History, RefreshCw, RotateCcw, Tag, X, XCircle } from 'lucide-react';
import { apiFetch, apiFetchWithRetry, extractErrorDetail } from '../lib/apiClient.ts';
import { formatDateTime } from '../lib/utils.ts';
import { countryName } from '../lib/countries.ts';
import {
  preProvisioningLabel, stateLabel, stateVariant,
  type AutopilotDetail, type AutopilotRequest, type AutopilotState,
} from '../lib/autopilot.ts';
import { useAuth } from '../context/authContext.tsx';
import { useToast } from '../context/toastContext.tsx';
import { Badge } from '../components/ui/badge.tsx';
import { Button } from '../components/ui/button.tsx';
import { Input } from '../components/ui/input.tsx';
import { Label } from '../components/ui/label.tsx';
import { Select } from '../components/ui/select.tsx';
import { Skeleton, TableSkeletonRows } from '../components/ui/skeleton.tsx';
import { EmptyState } from '../components/ui/empty-state.tsx';
import { CopyableId } from '../components/ui/copyable-id.tsx';
import { RelativeTime } from '../components/ui/relative-time.tsx';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '../components/ui/table.tsx';
import { ConfirmImpactDialog, type ConfirmImpactCopy } from '../components/ConfirmImpactDialog.tsx';

const IDLE_POLL_MS = 30_000;
const IMPORTING_POLL_MS = 5_000;
const MAX_BACKOFF_MS = 300_000;

function deviceLabel(r: AutopilotRequest): string {
  return `${r.manufacturer} ${r.model}`.trim();
}

/**
 * Autopilot approval queue. Devices submit their Windows Autopilot hardware hash from the boot
 * media; approvers decide here, and Imaging Core imports approved devices into Intune. Only open
 * requests are listed; handled ones move to the Autopilot registration history report.
 * Approver/Administrator can decide; Technician sees the queue read-only.
 */
export default function AutopilotRegistrationsPage(): React.ReactElement {
  const { canDecideAutopilot, canViewAutopilotReport } = useAuth();
  const navigate = useNavigate();
  const [pending, setPending] = useState<AutopilotRequest[] | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const failures = useRef(0);
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);

  const load = useCallback(async (): Promise<AutopilotRequest[] | null> => {
    try {
      const res = await apiFetchWithRetry('/api/autopilot/registrations', { credentials: 'include' });
      if (!res.ok) throw new Error(await extractErrorDetail(res, 'Could not load the approval queue.'));
      const data = await res.json() as AutopilotRequest[];
      setPending(data);
      setLoadError(null);
      failures.current = 0;
      return data;
    } catch (err) {
      failures.current += 1;
      // Keep showing the last good data; only an initial failure leaves the table empty.
      setLoadError(err instanceof Error ? err.message : 'Could not reach the server.');
      return null;
    }
  }, []);

  const schedule = useCallback((current: AutopilotRequest[] | null) => {
    if (timer.current) clearTimeout(timer.current);
    const importing = current?.some((r) => r.state === 'Importing') ?? false;
    const base = importing ? IMPORTING_POLL_MS : IDLE_POLL_MS;
    const delay = failures.current > 0 ? Math.min(base * 2 ** failures.current, MAX_BACKOFF_MS) : base;
    timer.current = setTimeout(() => {
      void load().then((data) => schedule(data));
    }, delay);
  }, [load]);

  const refresh = useCallback(async () => {
    const data = await load();
    schedule(data);
  }, [load, schedule]);

  useEffect(() => {
    void refresh();
    return () => { if (timer.current) clearTimeout(timer.current); };
  }, [refresh]);

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-start justify-between gap-4">
        <div className="max-w-3xl">
          <p className="text-sm text-muted-foreground">
            Approved devices are imported into Windows Autopilot.
          </p>
        </div>
        <div className="flex gap-2">
          {canViewAutopilotReport && (
            <Button variant="outline" onClick={() => navigate('/reports/autopilot-registrations')}>
              <History /> Handled requests
            </Button>
          )}
          <Button variant="outline" onClick={() => void refresh()}>
            <RefreshCw /> Refresh
          </Button>
        </div>
      </div>

      {loadError && (
        <div role="alert" className="flex items-start gap-3 rounded-md border border-destructive/40 bg-destructive/5 p-4">
          <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0 text-destructive" aria-hidden="true" />
          <div className="flex-1 space-y-1">
            <p className="text-sm font-medium">Could not refresh the approval queue</p>
            <p className="text-sm text-muted-foreground">{loadError}</p>
          </div>
          <Button variant="outline" onClick={() => void refresh()}><RefreshCw /> Try again</Button>
        </div>
      )}

      <PendingTable rows={pending} canDecide={canDecideAutopilot} onOpen={setSelectedId} />

      {selectedId && (
        <ReviewDialog
          requestId={selectedId}
          canDecide={canDecideAutopilot}
          onClose={() => setSelectedId(null)}
          onDecided={() => { setSelectedId(null); void refresh(); }}
        />
      )}
    </div>
  );
}

function PendingTable({ rows, canDecide, onOpen }: {
  rows: AutopilotRequest[] | null;
  canDecide: boolean;
  onOpen: (id: string) => void;
}): React.ReactElement {
  return (
    <div className="overflow-hidden rounded-md border border-border">
      <Table>
        <TableHeader>
          <TableRow className="hover:bg-transparent">
            <TableHead>Reference</TableHead>
            <TableHead>Serial number</TableHead>
            <TableHead>Device</TableHead>
            <TableHead>Location</TableHead>
            <TableHead>Submitted</TableHead>
            <TableHead>Status</TableHead>
            <TableHead className="w-[110px]"><span className="sr-only">Actions</span></TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {rows === null && <TableSkeletonRows columns={7} rows={3} />}
          {rows?.length === 0 && (
            <TableRow className="hover:bg-transparent">
              <TableCell colSpan={7} className="p-0">
                <EmptyState
                  icon={CheckCircle2}
                  title="No registrations waiting"
                  description="Devices submitted from boot media appear here automatically."
                />
              </TableCell>
            </TableRow>
          )}
          {rows?.map((r) => (
            <TableRow key={r.requestId}>
              <TableCell><CopyableId value={r.referenceCode} label="reference code" className="font-mono text-sm font-medium" /></TableCell>
              <TableCell><CopyableId value={r.serialNumber} label="device serial number" className="font-mono text-sm" /></TableCell>
              <TableCell>
                <span className="text-sm">{deviceLabel(r)}</span>
                {r.architecture === 'arm64' && <Badge variant="outline" className="ml-2">ARM64</Badge>}
                {r.preProvisioningReady === false && <Badge variant="warning" className="ml-2">No TPM data</Badge>}
              </TableCell>
              <TableCell className="text-sm">{r.locationName ?? <span className="text-muted-foreground">-</span>}</TableCell>
              <TableCell className="text-xs text-muted-foreground"><RelativeTime value={r.submittedAt} /></TableCell>
              <TableCell>
                <Badge variant={stateVariant(r.state)} dot>{stateLabel(r.state)}</Badge>
              </TableCell>
              <TableCell>
                <Button variant={canDecide && r.state !== 'Importing' ? 'default' : 'outline'} onClick={() => onOpen(r.requestId)}>
                  {canDecide && r.state !== 'Importing' ? 'Review' : 'View'}
                </Button>
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </div>
  );
}

type PendingAction = 'approve' | 'reject' | 'retry';

function ReviewDialog({ requestId, canDecide, onClose, onDecided }: {
  requestId: string;
  canDecide: boolean;
  onClose: () => void;
  onDecided: () => void;
}): React.ReactElement {
  const { notify } = useToast();
  const [detail, setDetail] = useState<AutopilotDetail | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [tagId, setTagId] = useState('');
  const [reason, setReason] = useState('');
  const [confirming, setConfirming] = useState<PendingAction | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    let cancelled = false;
    void (async () => {
      try {
        const res = await apiFetchWithRetry(`/api/autopilot/registrations/${requestId}`, { credentials: 'include' });
        if (cancelled) return;
        if (!res.ok) { setError(await extractErrorDetail(res, 'Could not load the request.')); return; }
        setDetail(await res.json() as AutopilotDetail);
      } catch {
        if (!cancelled) setError('Could not reach the server.');
      }
    })();
    return () => { cancelled = true; };
  }, [requestId]);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape' && !confirming && !busy) onClose(); };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [confirming, busy, onClose]);

  const request = detail?.request;
  const state: AutopilotState | undefined = request?.state;
  const decidable = canDecide && state === 'PendingApproval';
  const retryable = canDecide && state === 'ImportFailed';
  const selectedOption = detail?.groupTagOptions.find((o) => o.definitionId === tagId);
  const tagMissing = !!detail?.groupTagRequired && !selectedOption;

  const tagOptions = (detail?.groupTagOptions ?? []).map((o) => ({
    value: o.definitionId,
    label: o.resolvedValue ? `${o.resolvedValue}` : o.name,
    description: o.resolvedValue
      ? (o.kind === 'Template' ? `${o.name} (template ${o.value})` : o.name)
      : `Unavailable: ${o.unavailableReason ?? 'cannot be resolved for this device.'}`,
    disabled: !o.resolvedValue,
  }));

  const copy = (action: PendingAction): ConfirmImpactCopy => {
    const serial = request?.serialNumber ?? '';
    switch (action) {
      case 'approve':
        return {
          confirmTitle: 'Import this device into Autopilot?',
          impact: `Device ${serial} is imported into Windows Autopilot for the whole tenant${selectedOption?.resolvedValue ? ` with group tag ${selectedOption.resolvedValue}` : ' without a group tag'}. Autopilot profiles targeting it apply the next time the device runs Windows setup.${request?.preProvisioningReady === false ? ' Its hardware hash has no TPM 2.0 data, so pre-provisioning and self-deploying profiles will fail on it.' : ''} Undoing this requires deleting the device in Intune.`,
          confirmLabel: 'Approve and import',
          destructive: false,
        };
      case 'retry':
        return {
          confirmTitle: 'Retry the import?',
          impact: `The hardware hash for ${serial} is submitted to Intune again${request?.groupTag ? ` with group tag ${request.groupTag}` : ''}.`,
          confirmLabel: 'Retry import',
          destructive: false,
        };
      case 'reject':
        return {
          confirmTitle: 'Reject this request?',
          impact: `Device ${serial} is not imported and its hardware hash is deleted. The technician sees the rejection${reason.trim() ? ' and your reason' : ''} on the device. A new request needs a fresh submission from the boot media.`,
          confirmLabel: 'Reject',
          destructive: true,
        };
    }
  };

  const act = async (action: PendingAction) => {
    setBusy(true);
    try {
      const body = action === 'approve' ? { groupTagDefinitionId: tagId || undefined }
        : action === 'reject' ? { reason: reason.trim() || undefined }
        : {};
      const res = await apiFetch(`/api/autopilot/registrations/${requestId}/${action}`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        credentials: 'include',
        body: JSON.stringify(body),
      });
      if (!res.ok) {
        notify({ status: 'error', title: 'Action failed', description: await extractErrorDetail(res, 'The request could not be updated.') });
        setConfirming(null);
        return;
      }
      const updated = await res.json() as AutopilotDetail['request'];
      if (updated.state === 'ImportFailed') {
        notify({ status: 'error', title: 'Import could not start', description: updated.importErrorName ?? 'Microsoft Graph rejected the import.' });
      } else if (action === 'reject') {
        notify({ status: 'success', title: `Rejected ${updated.serialNumber}` });
      } else {
        notify({ status: 'success', title: `Importing ${updated.serialNumber}`, description: 'Intune usually finishes processing within a few minutes.' });
      }
      onDecided();
    } catch {
      notify({ status: 'error', title: 'Network error.', description: 'Could not reach the server.' });
      setConfirming(null);
    } finally {
      setBusy(false);
    }
  };

  return createPortal(
    <div className="fixed inset-0 z-40 flex items-center justify-center bg-black/60 p-4" role="dialog" aria-modal="true" aria-labelledby="autopilot-review-title">
      <div className="flex max-h-full w-full max-w-2xl flex-col rounded-lg border border-border bg-background shadow-xl">
        <div className="flex items-start justify-between gap-3 border-b border-border p-5">
          <div className="space-y-1">
            <h2 id="autopilot-review-title" className="text-base font-semibold">
              {request ? `Autopilot request ${request.referenceCode}` : 'Autopilot request'}
            </h2>
            {request && <Badge variant={stateVariant(request.state)} dot>{stateLabel(request.state)}</Badge>}
          </div>
          <Button variant="ghost" size="icon" onClick={onClose} aria-label="Close" disabled={busy}>
            <X />
          </Button>
        </div>

        <div className="space-y-5 overflow-y-auto p-5">
          {error && <p role="alert" className="text-sm text-destructive">{error}</p>}
          {!detail && !error && (
            <div className="grid grid-cols-2 gap-4">
              {Array.from({ length: 6 }).map((_, i) => <Skeleton key={i} className="h-10 w-full" />)}
            </div>
          )}

          {request && detail && (
            <>
              <dl className="grid grid-cols-1 gap-x-6 gap-y-4 sm:grid-cols-2">
                <Meta label="Serial number"><CopyableId value={request.serialNumber} label="device serial number" className="font-mono text-sm" wrap /></Meta>
                <Meta label="Device">{deviceLabel(request)} ({request.architecture})</Meta>
                <Meta label="Location">
                  {request.locationName ?? '-'}
                  {(detail.locationRegion || detail.locationCountryCode) && (
                    <span className="block text-xs text-muted-foreground">
                      {[detail.locationRegion, countryName(detail.locationCountryCode)].filter(Boolean).join(' \u00b7 ')}
                    </span>
                  )}
                </Meta>
                <Meta label="Submitted">{formatDateTime(request.submittedAt)}</Meta>
                <Meta label="Pre-provisioning">
                  {preProvisioningLabel(request.preProvisioningReady)}
                  {request.tpmVersion && <span className="block text-xs text-muted-foreground">TPM {request.tpmVersion}</span>}
                </Meta>
                {state === 'PendingApproval' && <Meta label="Expires">{formatDateTime(request.expiresAt)}</Meta>}
                {request.groupTag && <Meta label="Group tag">{request.groupTag}</Meta>}
                {request.decidedByUpn && <Meta label="Decided by">{request.decidedByUpn}{request.decidedAt ? `, ${formatDateTime(request.decidedAt)}` : ''}</Meta>}
                {request.rejectionReason && <Meta label="Rejection reason">{request.rejectionReason}</Meta>}
                {request.clientVersion && <Meta label="Client version">{request.clientVersion}</Meta>}
              </dl>

              {decidable && request.preProvisioningReady === false && (
                <div className="flex items-start gap-3 rounded-md border border-amber-500/40 bg-amber-500/10 p-4">
                  <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0 text-amber-700 dark:text-amber-400" aria-hidden="true" />
                  <div className="space-y-1">
                    <p className="text-sm font-medium">No TPM 2.0 data in this hash</p>
                    <p className="text-sm text-muted-foreground">
                      User-driven Autopilot works, but pre-provisioning and self-deploying profiles will fail. To fix it, reject the request,
                      turn on TPM 2.0 in the device firmware and submit the device again.
                    </p>
                  </div>
                </div>
              )}

              {state === 'ImportFailed' && (
                <div className="flex items-start gap-3 rounded-md border border-destructive/40 bg-destructive/5 p-4">
                  <XCircle className="mt-0.5 h-4 w-4 shrink-0 text-destructive" aria-hidden="true" />
                  <div className="space-y-1">
                    <p className="text-sm font-medium">The import failed</p>
                    <p className="text-sm text-muted-foreground break-words">{request.importErrorName ?? 'Intune did not accept the device.'}</p>
                  </div>
                </div>
              )}

              {state === 'Importing' && (
                <p className="flex items-center gap-2 text-sm text-muted-foreground">
                  <RefreshCw className="h-4 w-4 animate-spin" aria-hidden="true" />
                  Intune is processing the import. This page updates automatically.
                </p>
              )}

              {decidable && (
                <div className="space-y-4 rounded-md border border-border bg-muted/30 p-4">
                  <div className="space-y-2">
                    <Label htmlFor="autopilot-group-tag" className="flex items-center gap-2">
                      <Tag className="h-4 w-4 text-muted-foreground" aria-hidden="true" />
                      Group tag {detail.groupTagRequired ? '(required)' : '(optional)'}
                    </Label>
                    {detail.groupTagOptions.length === 0 ? (
                      <p className="text-sm text-muted-foreground">
                        {detail.groupTagRequired
                          ? 'A group tag is required, but none are defined. An administrator adds them under Configuration, Autopilot.'
                          : 'No group tags are defined. The device is imported without one.'}
                      </p>
                    ) : (
                      <Select
                        id="autopilot-group-tag"
                        value={tagId}
                        onValueChange={setTagId}
                        options={tagOptions}
                        placeholder={detail.groupTagRequired ? 'Select a group tag' : 'No group tag'}
                        allowEmpty={!detail.groupTagRequired}
                      />
                    )}
                  </div>
                  <div className="space-y-2">
                    <Label htmlFor="autopilot-reason">Rejection reason (optional)</Label>
                    <Input
                      id="autopilot-reason"
                      value={reason}
                      onChange={(e) => setReason(e.target.value)}
                      maxLength={500}
                      placeholder="Shown to the technician on the device"
                    />
                  </div>
                </div>
              )}
            </>
          )}
        </div>

        {(decidable || retryable) && (
          <div className="flex justify-end gap-2 border-t border-border px-5 py-3">
            <Button variant="outline" onClick={() => setConfirming('reject')} disabled={busy}
              className="text-destructive hover:bg-destructive/10 hover:text-destructive">
              Reject
            </Button>
            {decidable && (
              <Button onClick={() => setConfirming('approve')} disabled={busy || tagMissing}>
                <CheckCircle2 /> Approve
              </Button>
            )}
            {retryable && (
              <Button onClick={() => setConfirming('retry')} disabled={busy}>
                <RotateCcw /> Retry import
              </Button>
            )}
          </div>
        )}
      </div>

      {confirming && (
        <ConfirmImpactDialog
          copy={copy(confirming)}
          busy={busy}
          onCancel={() => setConfirming(null)}
          onConfirm={() => void act(confirming)}
          titleId="autopilot-confirm-title"
        />
      )}
    </div>,
    document.body,
  );
}

function Meta({ label, children }: { label: string; children: React.ReactNode }): React.ReactElement {
  return (
    <div className="min-w-0 space-y-1">
      <dt className="text-xs font-medium uppercase tracking-wider text-muted-foreground">{label}</dt>
      <dd className="text-sm break-words">{children}</dd>
    </div>
  );
}
