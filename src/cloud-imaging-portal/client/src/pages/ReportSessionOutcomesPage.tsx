import { useMemo, useState } from 'react';
import { FileDown, PieChart } from 'lucide-react';
import { formatDateTime } from '../lib/utils.ts';
import { toCsv, downloadBlob } from '../lib/csv.ts';
import { previousRange, previousRangeLabel, resolvePreset, type DateRange } from '../lib/dateRange.ts';
import { sortRows, useSort } from '../lib/tableSort.ts';
import { useSessionHistory } from '../hooks/useSessionHistory.ts';
import {
  SESSION_OUTCOME_COLORS,
  countOutcomes,
  durationMilliseconds,
  imagingStartTime,
  durationStats,
  formatDuration,
  locationFilterOptions,
  recordsForLocations,
  successRate,
  type SessionHistoryRecord,
} from '../lib/locationStatistics.ts';
import { SUMMARY_COLORS } from '../lib/reportSummary.ts';
import { useUserPreferences } from '../context/userPreferencesContext.tsx';
import { Button } from '../components/ui/button.tsx';
import { DateRangeSelect } from '../components/ui/date-range-select.tsx';
import { MultiSelect } from '../components/ui/multi-select.tsx';
import { ReportFilterBar, type AppliedFilter } from '../components/ReportFilterBar.tsx';
import { ReportLoadError } from '../components/ReportLoadError.tsx';
import { ReportSummary } from '../components/ReportSummary.tsx';
import { SortableHead } from '../components/ui/sortable-head.tsx';
import { TableSkeletonRows } from '../components/ui/skeleton.tsx';
import { Badge, type BadgeProps } from '../components/ui/badge.tsx';
import { CopyableId } from '../components/ui/copyable-id.tsx';
import { EmptyState } from '../components/ui/empty-state.tsx';
import { Table, TableHeader, TableBody, TableRow, TableCell } from '../components/ui/table.tsx';

/**
 * Turns a `SessionState` name into a display label. Defensively coerces rather than assuming a
 * string: this runs inside render, so a single unexpected value (a backend that serialized the
 * enum as an ordinal, say) would otherwise throw and replace the entire report with the route
 * error boundary instead of degrading one cell.
 */
function stateLabel(state: string): string {
  if (state === null || state === undefined) return '-';
  return String(state).replace('Session', '').replace(/([A-Z])/g, ' $1').trim();
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

const OUTCOME_OPTIONS = ['SessionCompleted', 'SessionFailed', 'SessionExpired', 'SessionNotAuthorized']
  .map(value => ({ value, label: stateLabel(value), dotClassName: SESSION_OUTCOME_COLORS[value] }));

/**
 * Device Outcomes report (Reports feature): breakdown of terminal device outcomes over a
 * configurable date range, backed by the durable SessionHistory audit table (not the live,
 * purge-limited "DeviceSessions" table).
 */
export default function ReportDeviceOutcomesPage(): React.ReactElement {
  const { locations } = useUserPreferences();
  const [range, setRange] = useState<DateRange>(() => resolvePreset('last30')!);
  const [locationIds, setLocationIds] = useState<string[]>([]);
  const [outcomes, setOutcomes] = useState<string[]>([]);
  const [attempt, setAttempt] = useState(0);
  // The API returns rows in table-storage enumeration order, which is not chronological, so the
  // report has to impose one.
  const [sort, toggleSort] = useSort<'state' | 'device' | 'model' | 'location' | 'started' | 'finished' | 'duration'>({ key: 'finished', dir: 'desc' });
  const { from, to } = range;
  const { records, failed } = useSessionHistory(range, attempt);
  const previous = useMemo(() => previousRange(range), [range]);
  const { records: previousRecords, failed: previousFailed } = useSessionHistory(previous, attempt);

  const locationOptions = useMemo(() => locationFilterOptions(locations, records ?? []), [locations, records]);
  const locationRecords = useMemo(() => recordsForLocations(records ?? [], locationIds), [records, locationIds]);
  const counts = useMemo(() => countOutcomes(locationRecords), [locationRecords]);
  const rate = successRate(counts);
  const durations = useMemo(() => durationStats(locationRecords.map(durationMilliseconds)), [locationRecords]);
  // Undefined while the previous period loads or when it could not be loaded: the figure then has no comparison.
  const previousRate = useMemo(
    () => (previousRecords === null || previousFailed ? undefined : successRate(countOutcomes(recordsForLocations(previousRecords, locationIds)))),
    [previousRecords, previousFailed, locationIds],
  );
  const previousLabel = previousRangeLabel(range);

  const sorted = useMemo(() => sortRows(
    locationRecords.filter(r => outcomes.length === 0 || outcomes.includes(r.finalState)),
    sort,
    {
      state:    (r: SessionHistoryRecord) => stateLabel(r.finalState),
      device:   (r: SessionHistoryRecord) => r.deviceSerialNumber,
      model:    (r: SessionHistoryRecord) => `${r.deviceManufacturer} ${r.deviceModel}`,
      location: (r: SessionHistoryRecord) => r.locationName ?? '',
      started:  (r: SessionHistoryRecord) => imagingStartTime(r),
      finished: (r: SessionHistoryRecord) => new Date(r.terminalAt).getTime(),
      duration: (r: SessionHistoryRecord) => durationMilliseconds(r) ?? 0,
    },
  ), [locationRecords, outcomes, sort]);

  const appliedFilters = useMemo<AppliedFilter[]>(() => {
    const chips: AppliedFilter[] = [];
    for (const id of locationIds) {
      const label = locationOptions.find(o => o.value === id)?.label ?? id;
      chips.push({ key: `location:${id}`, label: `Location: ${label}`, onClear: () => setLocationIds(ids => ids.filter(v => v !== id)) });
    }
    for (const value of outcomes) {
      chips.push({ key: `outcome:${value}`, label: `Outcome: ${stateLabel(value)}`, onClear: () => setOutcomes(values => values.filter(v => v !== value)) });
    }
    return chips;
  }, [locationIds, locationOptions, outcomes]);

  // Exports what is on screen, in the order it is on screen.
  const handleExport = () => {
    if (sorted.length === 0) return;
    const csv = toCsv(sorted, [
      { header: 'Session ID', accessor: r => r.sessionId },
      { header: 'Final State', accessor: r => stateLabel(r.finalState) },
      { header: 'Device Serial Number', accessor: r => r.deviceSerialNumber },
      { header: 'Manufacturer', accessor: r => r.deviceManufacturer },
      { header: 'Model', accessor: r => r.deviceModel },
      { header: 'Location', accessor: r => r.locationName ?? '' },
      { header: 'Started At', accessor: r => r.imagingStartedAt ?? '' },
      { header: 'Finished At', accessor: r => r.terminalAt },
      { header: 'Duration', accessor: r => formatDuration(durationMilliseconds(r)) },
    ]);
    downloadBlob(`device-outcomes-${from}-to-${to}.csv`, csv);
  };

  return (
    <div className="space-y-6">
      <ReportFilterBar
        applied={appliedFilters}
        onClearAll={() => { setLocationIds([]); setOutcomes([]); }}
        actions={
          <Button variant="outline" onClick={handleExport} disabled={sorted.length === 0}>
            <FileDown /> Export CSV
          </Button>
        }
      >
        <DateRangeSelect
          id="device-outcomes-range"
          aria-label="Date range"
          value={range}
          onValueChange={setRange}
          wrapperClassName="w-44"
        />
        <MultiSelect
          id="device-outcomes-location"
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
          id="device-outcomes-outcome"
          aria-label="Outcomes"
          values={outcomes}
          onValuesChange={setOutcomes}
          options={OUTCOME_OPTIONS}
          placeholder="All outcomes"
          countLabel={n => `${n} outcomes`}
          wrapperClassName="w-44"
        />
      </ReportFilterBar>

      {failed ? (
        <ReportLoadError
          title="Could not load the device outcomes"
          detail="The session history did not respond. Try again in a moment."
          onRetry={() => setAttempt(value => value + 1)}
        />
      ) : (
        <>
          <ReportSummary
            loading={records === null}
            total={counts.total}
            noun={['session', 'sessions']}
            parts={[
              { key: 'completed', label: 'Completed', count: counts.completed, colorClassName: SUMMARY_COLORS.success },
              { key: 'failed', label: 'Failed', count: counts.failed, colorClassName: SUMMARY_COLORS.negative },
              { key: 'expired', label: 'Expired', count: counts.expired, colorClassName: SUMMARY_COLORS.muted },
              { key: 'notAuthorized', label: 'Not authorized', count: counts.notAuthorized, colorClassName: SUMMARY_COLORS.warning },
            ]}
            emptyText={locationIds.length > 0 ? 'No imaging sessions finished in this period at the chosen locations.' : 'No imaging sessions finished in this period.'}
            figures={[
              {
                label: 'Success rate',
                value: rate === null ? null : `${rate}%`,
                detail: previousRate === undefined ? undefined : previousRate === null ? `No sessions in the ${previousLabel.toLowerCase()}` : `${previousLabel}: ${previousRate}%`,
              },
              {
                label: 'Average duration',
                value: durations.average === null ? null : formatDuration(durations.average),
                detail: durations.longest === null ? 'No sessions started imaging' : `Longest ${formatDuration(durations.longest)}`,
              },
            ]}
          />

          <div className="rounded-md border border-border overflow-hidden">
            <Table>
              <TableHeader>
                <TableRow className="hover:bg-transparent">
                  <SortableHead label="State" sortKey="state" sort={sort} onSort={toggleSort} />
                  <SortableHead label="Device" sortKey="device" sort={sort} onSort={toggleSort} />
                  <SortableHead label="Manufacturer / Model" sortKey="model" sort={sort} onSort={toggleSort} />
                  <SortableHead label="Location" sortKey="location" sort={sort} onSort={toggleSort} />
                  <SortableHead label="Started" sortKey="started" sort={sort} onSort={toggleSort} />
                  <SortableHead label="Finished" sortKey="finished" sort={sort} onSort={toggleSort} />
                  <SortableHead label="Duration" sortKey="duration" sort={sort} onSort={toggleSort} />
                </TableRow>
              </TableHeader>
              <TableBody>
                {records === null && <TableSkeletonRows columns={7} rows={4} />}
                {records !== null && sorted.length === 0 && (
                  <TableRow>
                    <TableCell colSpan={7}>
                      {records.length === 0 ? (
                        <EmptyState icon={PieChart} title="No session history recorded yet."
                          description="Completed, failed, expired, and not-authorized sessions will appear here as they occur." />
                      ) : (
                        <EmptyState icon={PieChart} title="No sessions match these filters" />
                      )}
                    </TableCell>
                  </TableRow>
                )}
                {sorted.map(r => (
                  <TableRow key={r.sessionId}>
                    <TableCell><Badge variant={stateBadgeVariant(r.finalState)} dot>{stateLabel(r.finalState)}</Badge></TableCell>
                    <TableCell>
                      <CopyableId value={r.deviceSerialNumber} label="device serial number" className="font-mono text-sm font-medium text-foreground" />
                    </TableCell>
                    <TableCell>{r.deviceManufacturer} {r.deviceModel}</TableCell>
                    <TableCell>{r.locationName ?? 'Unassigned'}</TableCell>
                    <TableCell>{r.imagingStartedAt ? formatDateTime(r.imagingStartedAt) : '-'}</TableCell>
                    <TableCell>{formatDateTime(r.terminalAt)}</TableCell>
                    <TableCell>{formatDuration(durationMilliseconds(r))}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </div>
        </>
      )}
    </div>
  );
}
