import { useEffect, useMemo, useState } from 'react';
import { FileDown, MapPin, Search } from 'lucide-react';
import { apiFetchWithRetry } from '../lib/apiClient.ts';
import { downloadBlob, toCsv } from '../lib/csv.ts';
import { formatDateTime } from '../lib/utils.ts';
import {
  countOutcomes,
  durationMilliseconds,
  formatDuration,
  recordsForLocation,
  type LocationHistoryRecord,
} from '../lib/locationStatistics.ts';
import { useUserPreferences } from '../context/userPreferencesContext.tsx';
import { Badge, type BadgeProps } from '../components/ui/badge.tsx';
import { Button } from '../components/ui/button.tsx';
import { Card, CardContent } from '../components/ui/card.tsx';
import { EmptyState } from '../components/ui/empty-state.tsx';
import { Input } from '../components/ui/input.tsx';
import { Label } from '../components/ui/label.tsx';
import { Select } from '../components/ui/select.tsx';
import { Skeleton, TableSkeletonRows } from '../components/ui/skeleton.tsx';
import { CopyableId } from '../components/ui/copyable-id.tsx';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '../components/ui/table.tsx';

const DEFAULT_WINDOW_DAYS = 30;

function isoDateInputValue(date: Date): string {
  return date.toISOString().slice(0, 10);
}

function stateLabel(state: string): string {
  return String(state).replace('Session', '').replace(/([A-Z])/g, ' $1').trim();
}

function stateBadgeVariant(state: string): BadgeProps['variant'] {
  switch (state) {
    case 'SessionCompleted':     return 'success';
    case 'SessionFailed':        return 'destructive';
    case 'SessionNotAuthorized': return 'warning';
    default:                     return 'muted';
  }
}

export default function ReportLocationStatisticsPage(): React.ReactElement {
  const today = useMemo(() => new Date(), []);
  const defaultFrom = useMemo(() => new Date(today.getTime() - DEFAULT_WINDOW_DAYS * 86_400_000), [today]);
  const { locations } = useUserPreferences();
  const [from, setFrom] = useState(isoDateInputValue(defaultFrom));
  const [to, setTo] = useState(isoDateInputValue(today));
  const [selectedLocationId, setSelectedLocationId] = useState('');
  const [outcome, setOutcome] = useState('');
  const [search, setSearch] = useState('');
  const [records, setRecords] = useState<LocationHistoryRecord[] | null>(null);
  const [loadError, setLoadError] = useState(false);
  const [reload, setReload] = useState(0);

  useEffect(() => {
    let cancelled = false;
    setRecords(null);
    setLoadError(false);
    void (async () => {
      try {
        const params = new URLSearchParams({
          from: new Date(`${from}T00:00:00Z`).toISOString(),
          to: new Date(`${to}T23:59:59Z`).toISOString(),
        });
        const response = await apiFetchWithRetry(`/api/session-history?${params.toString()}`, { credentials: 'include' });
        if (cancelled) return;
        if (!response.ok) {
          setRecords([]);
          setLoadError(true);
          return;
        }
        setRecords(await response.json() as LocationHistoryRecord[]);
      } catch {
        if (!cancelled) {
          setRecords([]);
          setLoadError(true);
        }
      }
    })();
    return () => { cancelled = true; };
  }, [from, to, reload]);

  const locationOptions = useMemo(() => {
    const names = new Map(locations.map(location => [location.locationId, location.name]));
    for (const record of records ?? []) {
      if (record.locationId && record.locationName && !names.has(record.locationId)) {
        names.set(record.locationId, record.locationName);
      }
    }
    return [...names.entries()]
      .map(([value, label]) => ({ value, label }))
      .sort((a, b) => a.label.localeCompare(b.label));
  }, [locations, records]);

  const locationRecords = useMemo(
    () => recordsForLocation(records ?? [], selectedLocationId),
    [records, selectedLocationId],
  );
  const counts = useMemo(() => countOutcomes(locationRecords), [locationRecords]);
  const successRate = counts.total > 0 ? Math.round(counts.completed * 100 / counts.total) : null;
  const averageDuration = useMemo(() => {
    const durations = locationRecords.map(durationMilliseconds).filter((value): value is number => value !== null);
    return durations.length > 0 ? durations.reduce((sum, value) => sum + value, 0) / durations.length : null;
  }, [locationRecords]);

  const detailRecords = useMemo(() => {
    const query = search.trim().toLocaleLowerCase();
    return locationRecords
      .filter(record => !outcome || record.finalState === outcome)
      .filter(record => !query || `${record.deviceSerialNumber} ${record.deviceManufacturer} ${record.deviceModel}`.toLocaleLowerCase().includes(query))
      .sort((a, b) => b.terminalAt.localeCompare(a.terminalAt));
  }, [locationRecords, outcome, search]);

  const handleExport = () => {
    if (detailRecords.length === 0) return;
    const csv = toCsv(detailRecords, [
      { header: 'Session ID', accessor: record => record.sessionId },
      { header: 'Location', accessor: record => record.locationName ?? '' },
      { header: 'Outcome', accessor: record => stateLabel(record.finalState) },
      { header: 'Device Serial Number', accessor: record => record.deviceSerialNumber },
      { header: 'Manufacturer', accessor: record => record.deviceManufacturer },
      { header: 'Model', accessor: record => record.deviceModel },
      { header: 'Created At', accessor: record => record.createdAt },
      { header: 'Terminal At', accessor: record => record.terminalAt },
      { header: 'Duration', accessor: record => formatDuration(durationMilliseconds(record)) },
    ]);
    downloadBlob(`location-statistics-${from}-to-${to}.csv`, csv);
  };

  const metrics = [
    { label: 'Total devices', value: counts.total },
    { label: 'Successful', value: counts.completed },
    { label: 'Failed', value: counts.failed },
    { label: 'Expired', value: counts.expired },
    { label: 'Not authorized', value: counts.notAuthorized },
    { label: 'Success rate', value: successRate === null ? '-' : `${successRate}%` },
    { label: 'Average duration', value: formatDuration(averageDuration) },
  ];

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-end justify-between gap-4">
        <div className="flex flex-wrap items-end gap-3">
          <div className="space-y-2">
            <Label htmlFor="location">Location</Label>
            <Select
              id="location"
              value={selectedLocationId}
              onValueChange={setSelectedLocationId}
              options={locationOptions}
              placeholder="All locations"
              allowEmpty
              wrapperClassName="w-56"
            />
          </div>
          <div className="space-y-2">
            <Label htmlFor="from">From</Label>
            <Input id="from" type="date" value={from} max={to} onChange={event => setFrom(event.target.value)} className="w-40" />
          </div>
          <div className="space-y-2">
            <Label htmlFor="to">To</Label>
            <Input id="to" type="date" value={to} min={from} onChange={event => setTo(event.target.value)} className="w-40" />
          </div>
        </div>
        <Button variant="secondary" onClick={handleExport} disabled={detailRecords.length === 0}>
          <FileDown /> Export CSV
        </Button>
      </div>

      {loadError && (
        <div className="flex items-center justify-between gap-4 border-y border-destructive/30 bg-destructive/10 px-4 py-3 text-sm text-destructive">
          <span>Location statistics could not be loaded.</span>
          <Button variant="outline" size="sm" onClick={() => setReload(value => value + 1)}>Retry</Button>
        </div>
      )}

      <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
        {metrics.map(metric => (
          <Card key={metric.label}>
            <CardContent className="p-4">
              <p className="text-xs text-muted-foreground">{metric.label}</p>
              <div className="mt-1 text-2xl font-semibold tabular-nums">
                {records === null ? <Skeleton className="h-7 w-12" /> : metric.value}
              </div>
            </CardContent>
          </Card>
        ))}
      </div>

      <div className="flex flex-wrap items-end gap-3">
        <div className="space-y-2">
          <Label htmlFor="outcome">Outcome</Label>
          <Select
            id="outcome"
            value={outcome}
            onValueChange={setOutcome}
            options={[
              { value: 'SessionCompleted', label: 'Successful' },
              { value: 'SessionFailed', label: 'Failed' },
              { value: 'SessionExpired', label: 'Expired' },
              { value: 'SessionNotAuthorized', label: 'Not authorized' },
            ]}
            placeholder="All outcomes"
            allowEmpty
            wrapperClassName="w-48"
          />
        </div>
        <div className="space-y-2">
          <Label htmlFor="device-search">Device</Label>
          <div className="relative">
            <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground" aria-hidden="true" />
            <Input
              id="device-search"
              value={search}
              onChange={event => setSearch(event.target.value)}
              placeholder="Serial, manufacturer, or model"
              className="w-72 pl-9"
            />
          </div>
        </div>
      </div>

      <div className="rounded-md border border-border overflow-hidden">
        <Table>
          <TableHeader>
            <TableRow className="hover:bg-transparent">
              <TableHead>Outcome</TableHead>
              <TableHead>Device</TableHead>
              <TableHead>Location</TableHead>
              <TableHead>Started</TableHead>
              <TableHead>Finished</TableHead>
              <TableHead>Duration</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {records === null && <TableSkeletonRows columns={6} rows={4} />}
            {records !== null && !loadError && detailRecords.length === 0 && (
              <TableRow>
                <TableCell colSpan={6}>
                  <EmptyState
                    icon={MapPin}
                    title="No matching devices"
                    description="No terminal device outcomes match the selected location and filters."
                  />
                </TableCell>
              </TableRow>
            )}
            {records !== null && detailRecords.map(record => (
              <TableRow key={record.sessionId}>
                <TableCell><Badge variant={stateBadgeVariant(record.finalState)} dot>{stateLabel(record.finalState)}</Badge></TableCell>
                <TableCell>
                  <CopyableId value={record.deviceSerialNumber} label="device serial number" className="font-mono text-sm font-medium text-foreground" />
                  <div className="text-xs text-muted-foreground">{record.deviceManufacturer} {record.deviceModel}</div>
                </TableCell>
                <TableCell>{record.locationName ?? 'Unassigned'}</TableCell>
                <TableCell>{formatDateTime(record.createdAt)}</TableCell>
                <TableCell>{formatDateTime(record.terminalAt)}</TableCell>
                <TableCell>{formatDuration(durationMilliseconds(record))}</TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </div>
    </div>
  );
}