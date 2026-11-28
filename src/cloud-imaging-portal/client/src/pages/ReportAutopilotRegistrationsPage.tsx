import { useCallback, useEffect, useMemo, useState } from 'react';
import { BadgeCheck, FileDown } from 'lucide-react';
import { apiFetchWithRetry, extractErrorDetail } from '../lib/apiClient.ts';
import { formatDateTime } from '../lib/utils.ts';
import { toCsv, downloadBlob } from '../lib/csv.ts';
import { resolvePreset, type DateRange } from '../lib/dateRange.ts';
import { sortRows, useSort } from '../lib/tableSort.ts';
import { useDebouncedValue } from '../hooks/useDebouncedValue.ts';
import { groupTagChange, preProvisioningLabel, stateLabel, stateVariant, type AutopilotRequest, type AutopilotState } from '../lib/autopilot.ts';
import { durationStats, formatDuration, locationFilterOptions, recordsForLocations } from '../lib/locationStatistics.ts';
import { SUMMARY_COLORS, share } from '../lib/reportSummary.ts';
import { useUserPreferences } from '../context/userPreferencesContext.tsx';
import { Button } from '../components/ui/button.tsx';
import { DateRangeSelect } from '../components/ui/date-range-select.tsx';
import { MultiSelect } from '../components/ui/multi-select.tsx';
import { SearchInput } from '../components/ui/search-input.tsx';
import { ReportFilterBar, type AppliedFilter } from '../components/ReportFilterBar.tsx';
import { ReportLoadError } from '../components/ReportLoadError.tsx';
import { ReportSummary } from '../components/ReportSummary.tsx';
import { SortableHead } from '../components/ui/sortable-head.tsx';
import { TableSkeletonRows } from '../components/ui/skeleton.tsx';
import { Badge } from '../components/ui/badge.tsx';
import { CopyableId } from '../components/ui/copyable-id.tsx';
import { EmptyState } from '../components/ui/empty-state.tsx';
import { Table, TableHeader, TableBody, TableRow, TableCell } from '../components/ui/table.tsx';

const OUTCOMES: readonly AutopilotState[] = ['Imported', 'AlreadyRegistered', 'GroupTagUpdated', 'Rejected', 'Expired'];

// Each dot takes the colour of the outcome's badge: the three approvals are green, the rest muted.
const outcomeColor = (state: AutopilotState): string => (stateVariant(state) === 'success' ? SUMMARY_COLORS.success : SUMMARY_COLORS.muted);

const OUTCOME_OPTIONS = OUTCOMES.map(value => ({ value, label: stateLabel(value), dotClassName: outcomeColor(value) }));

function deviceLabel(r: AutopilotRequest): string {
  return `${r.manufacturer} ${r.model}`.trim();
}

/** The one line that explains why a request ended the way it did, if anything does. */
function outcomeNote(r: AutopilotRequest): string | null {
  if (r.state === 'Rejected') return r.rejectionReason ?? null;
  return r.importErrorName ?? null;
}

/** Time from submission to the approver's decision, for requests someone decided. */
function waitForApproval(r: AutopilotRequest): number | null {
  if (!r.decidedAt) return null;
  const wait = Date.parse(r.decidedAt) - Date.parse(r.submittedAt);
  return Number.isFinite(wait) && wait >= 0 ? wait : null;
}

/**
 * Audit report of handled Autopilot registration requests: imported, already registered,
 * rejected and expired. Handled requests leave the approval queue immediately and are kept here
 * until the retention period set under Configuration, Autopilot deletes them.
 */
export default function ReportAutopilotRegistrationsPage(): React.ReactElement {
  const { locations } = useUserPreferences();
  const [range, setRange] = useState<DateRange>(() => resolvePreset('last30')!);
  const [records, setRecords] = useState<AutopilotRequest[] | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [locationIds, setLocationIds] = useState<string[]>([]);
  const [outcomes, setOutcomes] = useState<string[]>([]);
  const [search, setSearch] = useState('');
  const debouncedSearch = useDebouncedValue(search);
  const [attempt, setAttempt] = useState(0);
  const [sort, toggleSort] = useSort<'outcome' | 'reference' | 'serial' | 'device' | 'location' | 'groupTag' | 'decidedBy' | 'closed'>({ key: 'closed', dir: 'desc' });
  const { from, to } = range;

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

  const locationOptions = useMemo(() => locationFilterOptions(locations, records ?? []), [locations, records]);
  // The summary covers the period and the chosen locations; outcomes and the search narrow the table.
  const locationRecords = useMemo(() => recordsForLocations(records ?? [], locationIds), [records, locationIds]);

  const filtered = useMemo(() => {
    const needle = debouncedSearch.trim().toLowerCase();
    const matching = locationRecords.filter((r) =>
      (outcomes.length === 0 || outcomes.includes(r.state))
      && (!needle || [r.serialNumber, r.referenceCode, deviceLabel(r), r.locationName ?? '', r.groupTag ?? '', r.decidedByUpn ?? '']
        .some((v) => v.toLowerCase().includes(needle))));

    return sortRows(matching, sort, {
      outcome:   (r: AutopilotRequest) => stateLabel(r.state),
      reference: (r: AutopilotRequest) => r.referenceCode,
      serial:    (r: AutopilotRequest) => r.serialNumber,
      device:    (r: AutopilotRequest) => deviceLabel(r),
      location:  (r: AutopilotRequest) => r.locationName ?? '',
      groupTag:  (r: AutopilotRequest) => r.groupTag ?? '',
      decidedBy: (r: AutopilotRequest) => r.decidedByUpn ?? '',
      closed:    (r: AutopilotRequest) => new Date(r.updatedAt).getTime(),
    });
  }, [locationRecords, outcomes, debouncedSearch, sort]);

  const appliedFilters = useMemo<AppliedFilter[]>(() => {
    const chips: AppliedFilter[] = [];
    if (search) chips.push({ key: 'search', label: `Search: ${search}`, onClear: () => setSearch('') });
    for (const id of locationIds) {
      const label = locationOptions.find(o => o.value === id)?.label ?? id;
      chips.push({ key: `location:${id}`, label: `Location: ${label}`, onClear: () => setLocationIds(ids => ids.filter(v => v !== id)) });
    }
    for (const value of outcomes) {
      chips.push({ key: `outcome:${value}`, label: `Outcome: ${stateLabel(value as AutopilotState)}`, onClear: () => setOutcomes(values => values.filter(v => v !== value)) });
    }
    return chips;
  }, [search, locationIds, locationOptions, outcomes]);

  const counts = useMemo(() => {
    const c: Record<AutopilotState, number> = { PendingApproval: 0, Importing: 0, Imported: 0, ImportFailed: 0, Rejected: 0, Expired: 0, AlreadyRegistered: 0, GroupTagUpdated: 0 };
    for (const r of locationRecords) c[r.state] = (c[r.state] ?? 0) + 1;
    return c;
  }, [locationRecords]);
  const waits = useMemo(() => durationStats(locationRecords.map(waitForApproval)), [locationRecords]);
  const total = locationRecords.length;
  const tagShare = share(counts.GroupTagUpdated, total);

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
      { header: 'Previous Group Tag', accessor: (r) => (r.state === 'GroupTagUpdated' ? r.previousGroupTag ?? '' : null) },
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

      <ReportFilterBar
        applied={appliedFilters}
        onClearAll={() => { setSearch(''); setLocationIds([]); setOutcomes([]); }}
        actions={
          <Button variant="outline" onClick={handleExport} disabled={filtered.length === 0}>
            <FileDown /> Export CSV
          </Button>
        }
      >
        <SearchInput
          id="autopilot-report-search"
          value={search}
          onValueChange={setSearch}
          placeholder="Serial, reference, location or approver"
          wrapperClassName="w-96"
        />
        <DateRangeSelect
          id="autopilot-report-range"
          aria-label="Closed between"
          value={range}
          onValueChange={setRange}
          wrapperClassName="w-44"
        />
        <MultiSelect
          id="autopilot-report-location"
          aria-label="Locations"
          values={locationIds}
          onValuesChange={setLocationIds}
          options={locationOptions}
          placeholder="All locations"
          countLabel={n => `${n} locations`}
          searchPlaceholder="Search locations"
          wrapperClassName="w-52"
        />
        <MultiSelect
          id="autopilot-report-outcome"
          aria-label="Outcomes"
          values={outcomes}
          onValuesChange={setOutcomes}
          options={OUTCOME_OPTIONS}
          placeholder="All outcomes"
          countLabel={n => `${n} outcomes`}
          wrapperClassName="w-44"
        />
      </ReportFilterBar>

      {loadError && (
        <ReportLoadError title="Could not load handled registrations" detail={loadError} onRetry={retry} />
      )}

      {!loadError && (
        <>
          <ReportSummary
            loading={records === null}
            total={total}
            noun={['handled request', 'handled requests']}
            parts={OUTCOMES.map(state => ({
              key: state,
              label: stateLabel(state),
              count: counts[state],
              colorClassName: outcomeColor(state),
              // Counted in the bar, but its own figure shows it, so the legend keeps to one line.
              inLegend: state !== 'GroupTagUpdated',
            }))}
            emptyText={locationIds.length > 0 ? 'No requests were handled in this period at the chosen locations.' : 'No requests were handled in this period.'}
            figures={[
              {
                label: 'Average wait for approval',
                value: waits.average === null ? null : formatDuration(waits.average),
                detail: waits.longest === null ? 'No decided requests' : `Longest ${formatDuration(waits.longest)}`,
              },
              {
                label: 'Group tag updated',
                value: total === 0 ? null : String(counts.GroupTagUpdated),
                detail: tagShare === null ? 'No handled requests' : `${tagShare}% of handled requests`,
              },
            ]}
          />

          <div className="overflow-hidden rounded-md border border-border">
            <Table>
              <TableHeader>
                <TableRow className="hover:bg-transparent">
                  <SortableHead label="Outcome" sortKey="outcome" sort={sort} onSort={toggleSort} />
                  <SortableHead label="Reference" sortKey="reference" sort={sort} onSort={toggleSort} />
                  <SortableHead label="Serial number" sortKey="serial" sort={sort} onSort={toggleSort} />
                  <SortableHead label="Device" sortKey="device" sort={sort} onSort={toggleSort} />
                  <SortableHead label="Location" sortKey="location" sort={sort} onSort={toggleSort} />
                  <SortableHead label="Group tag" sortKey="groupTag" sort={sort} onSort={toggleSort} />
                  <SortableHead label="Decided by" sortKey="decidedBy" sort={sort} onSort={toggleSort} />
                  <SortableHead label="Closed" sortKey="closed" sort={sort} onSort={toggleSort} />
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
                        description={records.length === 0 ? 'Imported, already registered, group tag updated, rejected and expired requests are listed here once they leave the approval queue.' : undefined}
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
                      <TableCell className="text-sm">{groupTagChange(r) ?? <span className="text-muted-foreground">-</span>}</TableCell>
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
