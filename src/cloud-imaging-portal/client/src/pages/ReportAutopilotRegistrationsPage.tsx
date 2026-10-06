import { useCallback, useEffect, useMemo, useState } from 'react';
import { AlertTriangle, BadgeCheck, FileDown, RefreshCw } from 'lucide-react';
import { apiFetchWithRetry, extractErrorDetail } from '../lib/apiClient.ts';
import { formatDateTime } from '../lib/utils.ts';
import { toCsv, downloadBlob } from '../lib/csv.ts';
import { preProvisioningLabel, stateLabel, stateVariant, type AutopilotRequest } from '../lib/autopilot.ts';
import { Button } from '../components/ui/button.tsx';
import { Card, CardContent } from '../components/ui/card.tsx';
import { Input } from '../components/ui/input.tsx';
import { Label } from '../components/ui/label.tsx';
import { Select } from '../components/ui/select.tsx';
import { Skeleton, TableSkeletonRows } from '../components/ui/skeleton.tsx';
import { Badge } from '../components/ui/badge.tsx';
import { CopyableId } from '../components/ui/copyable-id.tsx';
import { EmptyState } from '../components/ui/empty-state.tsx';
import { Table, TableHeader, TableBody, TableRow, TableHead, TableCell } from '../components/ui/table.tsx';

const DEFAULT_WINDOW_DAYS = 30;

const OUTCOME_FILTERS: { value: string; label: string }[] = [
  { value: '', label: 'All outcomes' },
  { value: 'Imported', label: 'Imported' },
  { value: 'AlreadyRegistered', label: 'Already registered' },
  { value: 'Rejected', label: 'Rejected' },
  { value: 'Expired', label: 'Expired' },
];

function isoDateInputValue(date: Date): string {
  return date.toISOString().slice(0, 10);
}

function deviceLabel(r: AutopilotRequest): string {
  return `${r.manufacturer} ${r.model}`.trim();
}

/** The one line that explains why a request ended the way it did, if anything does. */
function outcomeNote(r: AutopilotRequest): string | null {
  if (r.state === 'Rejected') return r.rejectionReason ?? null;
  return r.importErrorName ?? null;
}

/**
 * Audit report of handled Autopilot registration requests: imported, already registered,
 * rejected and expired. Handled requests leave the approval queue immediately and are kept here
 * until the retention period set under Configuration, Autopilot deletes them.
 */
export default function ReportAutopilotRegistrationsPage(): React.ReactElement {
  const today = useMemo(() => new Date(), []);
  const [from, setFrom] = useState(isoDateInputValue(new Date(today.getTime() - DEFAULT_WINDOW_DAYS * 86_400_000)));
  const [to, setTo] = useState(isoDateInputValue(today));
  const [records, setRecords] = useState<AutopilotRequest[] | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [outcome, setOutcome] = useState('');
  const [search, setSearch] = useState('');
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    let cancelled = false;
    setRecords(null);
    setLoadError(null);
    void (async () => {
      try {
        const params = new URLSearchParams({
          from: new Date(`${from}T00:00:00Z`).toISOString(),
          to: new Date(`${to}T23:59:59.999Z`).toISOString(),
        });
        const res = await apiFetchWithRetry(`/api/autopilot/history?${params.toString()}`, { credentials: 'include' });
        if (cancelled) return;
        if (!res.ok) {
          setLoadError(await extractErrorDetail(res, 'Could not load handled registrations.'));
          return;
        }
        setRecords(await res.json() as AutopilotRequest[]);
      } catch {
        if (!cancelled) setLoadError('Could not reach the server.');
      }
    })();
    return () => { cancelled = true; };
  }, [from, to, attempt]);

  const retry = useCallback(() => setAttempt((n) => n + 1), []);

  const filtered = useMemo(() => {
    const needle = search.trim().toLowerCase();
    return (records ?? []).filter((r) =>
      (!outcome || r.state === outcome)
      && (!needle || [r.serialNumber, r.referenceCode, deviceLabel(r), r.locationName ?? '', r.groupTag ?? '', r.decidedByUpn ?? '']
        .some((v) => v.toLowerCase().includes(needle))));
  }, [records, outcome, search]);

  const counts = useMemo(() => {
    const c = { total: 0, imported: 0, alreadyRegistered: 0, rejected: 0, expired: 0 };
    for (const r of records ?? []) {
      c.total++;
      if (r.state === 'Imported') c.imported++;
      else if (r.state === 'AlreadyRegistered') c.alreadyRegistered++;
      else if (r.state === 'Rejected') c.rejected++;
      else if (r.state === 'Expired') c.expired++;
    }
    return c;
  }, [records]);

  const handleExport = () => {
    const csv = toCsv(filtered, [
      { header: 'Reference', accessor: (r) => r.referenceCode },
      { header: 'Outcome', accessor: (r) => stateLabel(r.state) },
      { header: 'Serial Number', accessor: (r) => r.serialNumber },
      { header: 'Manufacturer', accessor: (r) => r.manufacturer },
      { header: 'Model', accessor: (r) => r.model },
      { header: 'Architecture', accessor: (r) => r.architecture },
      { header: 'TPM Version', accessor: (r) => r.tpmVersion },
      { header: 'Pre-provisioning', accessor: (r) => preProvisioningLabel(r.preProvisioningReady) },
      { header: 'Location', accessor: (r) => r.locationName },
      { header: 'Group Tag', accessor: (r) => r.groupTag },
      { header: 'Decided By', accessor: (r) => r.decidedByUpn },
      { header: 'Decided At', accessor: (r) => r.decidedAt },
      { header: 'Rejection Reason', accessor: (r) => r.rejectionReason },
      { header: 'Import Error', accessor: (r) => r.importErrorName },
      { header: 'Import Attempts', accessor: (r) => r.importAttempts },
      { header: 'Client Version', accessor: (r) => r.clientVersion },
      { header: 'Submitted At', accessor: (r) => r.submittedAt },
      { header: 'Closed At', accessor: (r) => r.updatedAt },
    ]);
    downloadBlob(`autopilot-registrations-${from}-to-${to}.csv`, csv);
  };

  return (
    <div className="space-y-6">
      <div className="max-w-3xl space-y-1">
        <div className="flex items-center gap-2">
          <BadgeCheck size={24} className="text-primary" aria-hidden="true" />
          <h2 className="text-lg font-semibold">Autopilot registration history</h2>
        </div>
        <p className="text-sm text-muted-foreground">
          Every handled request, with who decided it and the group tag it was imported with. Requests are deleted after the retention period an administrator sets.
        </p>
      </div>

      <div className="flex flex-wrap items-end justify-between gap-4">
        <div className="flex flex-wrap items-end gap-3">
          <div className="space-y-2">
            <Label htmlFor="autopilot-report-from">Closed from</Label>
            <Input id="autopilot-report-from" type="date" value={from} max={to} onChange={(e) => setFrom(e.target.value)} className="w-40" />
          </div>
          <div className="space-y-2">
            <Label htmlFor="autopilot-report-to">To</Label>
            <Input id="autopilot-report-to" type="date" value={to} min={from} onChange={(e) => setTo(e.target.value)} className="w-40" />
          </div>
          <div className="space-y-2">
            <Label htmlFor="autopilot-report-outcome">Outcome</Label>
            <Select id="autopilot-report-outcome" value={outcome} onValueChange={setOutcome} options={OUTCOME_FILTERS} wrapperClassName="w-48" />
          </div>
          <div className="space-y-2">
            <Label htmlFor="autopilot-report-search">Search</Label>
            <Input id="autopilot-report-search" value={search} onChange={(e) => setSearch(e.target.value)} placeholder="Serial, reference, location, tag or approver" className="w-72" />
          </div>
        </div>
        <Button variant="outline" onClick={handleExport} disabled={filtered.length === 0}>
          <FileDown /> Export CSV
        </Button>
      </div>

      {loadError && (
        <div role="alert" className="flex items-start gap-3 rounded-md border border-destructive/40 bg-destructive/5 p-4">
          <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0 text-destructive" aria-hidden="true" />
          <div className="flex-1 space-y-1">
            <p className="text-sm font-medium">Could not load handled registrations</p>
            <p className="text-sm text-muted-foreground">{loadError}</p>
          </div>
          <Button variant="outline" onClick={retry}><RefreshCw /> Try again</Button>
        </div>
      )}

      {!loadError && (
        <>
          <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-5">
            {[
              { label: 'Handled requests', value: counts.total },
              { label: 'Imported', value: counts.imported },
              { label: 'Already registered', value: counts.alreadyRegistered },
              { label: 'Rejected', value: counts.rejected },
              { label: 'Expired', value: counts.expired },
            ].map((stat) => (
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

          <div className="overflow-hidden rounded-md border border-border">
            <Table>
              <TableHeader>
                <TableRow className="hover:bg-transparent">
                  <TableHead>Outcome</TableHead>
                  <TableHead>Reference</TableHead>
                  <TableHead>Serial number</TableHead>
                  <TableHead>Device</TableHead>
                  <TableHead>Location</TableHead>
                  <TableHead>Group tag</TableHead>
                  <TableHead>Decided by</TableHead>
                  <TableHead>Closed</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {records === null && <TableSkeletonRows columns={8} rows={4} />}
                {records !== null && filtered.length === 0 && (
                  <TableRow className="hover:bg-transparent">
                    <TableCell colSpan={8} className="p-0">
                      <EmptyState
                        icon={BadgeCheck}
                        title={records.length === 0 ? 'No handled requests in this period' : 'No requests match these filters'}
                        description={records.length === 0 ? 'Imported, already registered, rejected and expired requests are listed here once they leave the approval queue.' : undefined}
                      />
                    </TableCell>
                  </TableRow>
                )}
                {filtered.map((r) => {
                  const note = outcomeNote(r);
                  return (
                    <TableRow key={r.requestId}>
                      <TableCell><Badge variant={stateVariant(r.state)} dot>{stateLabel(r.state)}</Badge></TableCell>
                      <TableCell><CopyableId value={r.referenceCode} label="reference code" className="font-mono text-sm" /></TableCell>
                      <TableCell><CopyableId value={r.serialNumber} label="device serial number" className="font-mono text-sm" /></TableCell>
                      <TableCell className="text-sm">{deviceLabel(r)}</TableCell>
                      <TableCell className="text-sm">{r.locationName ?? <span className="text-muted-foreground">-</span>}</TableCell>
                      <TableCell className="text-sm">{r.groupTag ?? <span className="text-muted-foreground">-</span>}</TableCell>
                      <TableCell className="text-sm">
                        {r.decidedByUpn ?? <span className="text-muted-foreground">-</span>}
                        {note && <span className="block max-w-xs break-words text-xs text-muted-foreground">{note}</span>}
                      </TableCell>
                      <TableCell className="text-xs text-muted-foreground">{formatDateTime(r.updatedAt)}</TableCell>
                    </TableRow>
                  );
                })}
              </TableBody>
            </Table>
          </div>
        </>
      )}
    </div>
  );
}
