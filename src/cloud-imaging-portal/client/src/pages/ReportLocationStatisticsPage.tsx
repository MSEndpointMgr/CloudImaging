import { useMemo, useState } from 'react';
import { FileDown, MapPin } from 'lucide-react';
import { downloadBlob, toCsv } from '../lib/csv.ts';
import { formatDateTime } from '../lib/utils.ts';
import { previousRange, previousRangeLabel, resolvePreset, type DateRange } from '../lib/dateRange.ts';
import { sortRows, useSort } from '../lib/tableSort.ts';
import { useDebouncedValue } from '../hooks/useDebouncedValue.ts';
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
import { Badge, type BadgeProps } from '../components/ui/badge.tsx';
import { Button } from '../components/ui/button.tsx';
import { DateRangeSelect } from '../components/ui/date-range-select.tsx';
import { MultiSelect } from '../components/ui/multi-select.tsx';
import { SearchInput } from '../components/ui/search-input.tsx';
import { ReportFilterBar, type AppliedFilter } from '../components/ReportFilterBar.tsx';
import { ReportLoadError } from '../components/ReportLoadError.tsx';
import { ReportSummary } from '../components/ReportSummary.tsx';
import { SortableHead } from '../components/ui/sortable-head.tsx';
import { EmptyState } from '../components/ui/empty-state.tsx';
import { TableSkeletonRows } from '../components/ui/skeleton.tsx';
import { CopyableId } from '../components/ui/copyable-id.tsx';
import { Table, TableBody, TableCell, TableHeader, TableRow } from '../components/ui/table.tsx';

// This report calls a completed session successful, so its filter, summary, badges and export all do.
const OUTCOME_LABELS: Record<string, string> = {
  SessionCompleted: 'Successful',
  SessionFailed: 'Failed',
  SessionExpired: 'Expired',
  SessionNotAuthorized: 'Not authorized',
};

const OUTCOME_OPTIONS = Object.entries(OUTCOME_LABELS).map(([value, label]) => ({ value, label, dotClassName: SESSION_OUTCOME_COLORS[value] }));

function outcomeLabel(state: string): string {
  return OUTCOME_LABELS[state] ?? String(state);
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
  const { locations } = useUserPreferences();
  const [range, setRange] = useState<DateRange>(() => resolvePreset('last30')!);
  const [locationIds, setLocationIds] = useState<string[]>([]);
  const [outcomes, setOutcomes] = useState<string[]>([]);
  const [search, setSearch] = useState('');
  const debouncedSearch = useDebouncedValue(search);
  const [attempt, setAttempt] = useState(0);
  const [sort, toggleSort] = useSort<'outcome' | 'device' | 'location' | 'started' | 'finished' | 'duration'>({ key: 'finished', dir: 'desc' });
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

  const detailRecords = useMemo(() => {
    const query = debouncedSearch.trim().toLocaleLowerCase();
    const matching = locationRecords
      .filter(record => outcomes.length === 0 || outcomes.includes(record.finalState))
      .filter(record => !query || `${record.deviceSerialNumber} ${record.deviceManufacturer} ${record.deviceModel}`.toLocaleLowerCase().includes(query));

    return sortRows(matching, sort, {
      outcome:  (record: SessionHistoryRecord) => outcomeLabel(record.finalState),
      device:   (record: SessionHistoryRecord) => record.deviceSerialNumber,
      location: (record: SessionHistoryRecord) => record.locationName ?? '',
      started:  (record: SessionHistoryRecord) => imagingStartTime(record),
      finished: (record: SessionHistoryRecord) => new Date(record.terminalAt).getTime(),
      duration: (record: SessionHistoryRecord) => durationMilliseconds(record) ?? 0,
    });
  }, [locationRecords, outcomes, debouncedSearch, sort]);

  const appliedFilters = useMemo<AppliedFilter[]>(() => {
    const chips: AppliedFilter[] = [];
    for (const id of locationIds) {
      const label = locationOptions.find(o => o.value === id)?.label ?? id;
      chips.push({ key: `location:${id}`, label: `Location: ${label}`, onClear: () => setLocationIds(ids => ids.filter(v => v !== id)) });
    }
    for (const value of outcomes) {
      chips.push({ key: `outcome:${value}`, label: `Outcome: ${outcomeLabel(value)}`, onClear: () => setOutcomes(values => values.filter(v => v !== value)) });
    }
    if (search) chips.push({ key: 'search', label: `Device: ${search}`, onClear: () => setSearch('') });
    return chips;
  }, [locationIds, locationOptions, outcomes, search]);

  const handleExport = () => {
    if (detailRecords.length === 0) return;
    const csv = toCsv(detailRecords, [
      { header: 'Session ID', accessor: record => record.sessionId },
      { header: 'Location', accessor: record => record.locationName ?? '' },
      { header: 'Outcome', accessor: record => outcomeLabel(record.finalState) },
      { header: 'Device Serial Number', accessor: record => record.deviceSerialNumber },
      { header: 'Manufacturer', accessor: record => record.deviceManufacturer },
      { header: 'Model', accessor: record => record.deviceModel },
      { header: 'Started At', accessor: record => record.imagingStartedAt ?? '' },
      { header: 'Finished At', accessor: record => record.terminalAt },
      { header: 'Duration', accessor: record => formatDuration(durationMilliseconds(record)) },
    ]);
    downloadBlob(`location-statistics-${from}-to-${to}.csv`, csv);
  };

  return (
    <div className="space-y-6">
      <ReportFilterBar
        applied={appliedFilters}
        onClearAll={() => { setLocationIds([]); setOutcomes([]); setSearch(''); }}
        actions={
          <Button variant="outline" onClick={handleExport} disabled={detailRecords.length === 0}>
            <FileDown /> Export CSV
          </Button>
        }
      >
        <SearchInput
          id="device-search"
          value={search}
          onValueChange={setSearch}
          placeholder="Serial, manufacturer or model"
          wrapperClassName="w-72"
        />
        <DateRangeSelect
          id="location-statistics-range"
          aria-label="Date range"
          value={range}
          onValueChange={setRange}
          wrapperClassName="w-44"
        />
        <MultiSelect
          id="location"
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
          id="outcome"
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
          title="Could not load the location statistics"
          detail="The session history did not respond. Try again in a moment."
          onRetry={() => setAttempt(value => value + 1)}
        />
      ) : (
        <>
          <ReportSummary
            loading={records === null}
            total={counts.total}
            noun={['device', 'devices']}
            parts={[
              { key: 'completed', label: 'Successful', count: counts.completed, colorClassName: SUMMARY_COLORS.success },
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
                  <SortableHead label="Outcome" sortKey="outcome" sort={sort} onSort={toggleSort} />
                  <SortableHead label="Device" sortKey="device" sort={sort} onSort={toggleSort} />
                  <SortableHead label="Location" sortKey="location" sort={sort} onSort={toggleSort} />
                  <SortableHead label="Started" sortKey="started" sort={sort} onSort={toggleSort} />
                  <SortableHead label="Finished" sortKey="finished" sort={sort} onSort={toggleSort} />
                  <SortableHead label="Duration" sortKey="duration" sort={sort} onSort={toggleSort} />
                </TableRow>
              </TableHeader>
              <TableBody>
                {records === null && <TableSkeletonRows columns={6} rows={4} />}
                {records !== null && detailRecords.length === 0 && (
                  <TableRow>
                    <TableCell colSpan={6}>
                      <EmptyState
                        icon={MapPin}
                        title="No matching devices"
                        description="No finished sessions match the chosen locations and filters."
                      />
                    </TableCell>
                  </TableRow>
                )}
                {records !== null && detailRecords.map(record => (
                  <TableRow key={record.sessionId}>
                    <TableCell><Badge variant={stateBadgeVariant(record.finalState)} dot>{outcomeLabel(record.finalState)}</Badge></TableCell>
                    <TableCell>
                      <CopyableId value={record.deviceSerialNumber} label="device serial number" className="font-mono text-sm font-medium text-foreground" />
                      <div className="text-xs text-muted-foreground">{record.deviceManufacturer} {record.deviceModel}</div>
                    </TableCell>
                    <TableCell>{record.locationName ?? 'Unassigned'}</TableCell>
                    <TableCell>{record.imagingStartedAt ? formatDateTime(record.imagingStartedAt) : '-'}</TableCell>
                    <TableCell>{formatDateTime(record.terminalAt)}</TableCell>
                    <TableCell>{formatDuration(durationMilliseconds(record))}</TableCell>
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