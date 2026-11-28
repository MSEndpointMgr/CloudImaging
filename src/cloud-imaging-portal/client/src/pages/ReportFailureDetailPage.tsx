import { useMemo, useState } from 'react';
import { FileDown, AlertOctagon } from 'lucide-react';
import { formatDateTime } from '../lib/utils.ts';
import { toCsv, downloadBlob } from '../lib/csv.ts';
import { resolvePreset, type DateRange } from '../lib/dateRange.ts';
import { sortRows, useSort } from '../lib/tableSort.ts';
import { useSessionHistory } from '../hooks/useSessionHistory.ts';
import {
  SESSION_OUTCOME_COLORS,
  countOutcomes,
  locationFilterOptions,
  recordsForLocations,
  type SessionHistoryRecord,
} from '../lib/locationStatistics.ts';
import { SUMMARY_COLORS, plural, share } from '../lib/reportSummary.ts';
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
import { EmptyState } from '../components/ui/empty-state.tsx';
import { CopyableId } from '../components/ui/copyable-id.tsx';
import { Table, TableHeader, TableBody, TableRow, TableCell } from '../components/ui/table.tsx';

const FAILURE_STATES = ['SessionFailed', 'SessionExpired', 'SessionNotAuthorized'];

/** Display label for a `SessionState` name. Total, for the reason given on the same helper in ReportSessionOutcomesPage. */
function stateLabel(state: string): string {
  if (state === null || state === undefined) return '-';
  return String(state).replace('Session', '').replace(/([A-Z])/g, ' $1').trim();
}

function stateBadgeVariant(state: string): BadgeProps['variant'] {
  switch (state) {
    case 'SessionFailed':        return 'destructive';
    case 'SessionNotAuthorized': return 'warning';
    case 'SessionExpired':       return 'muted';
    default:                     return 'muted';
  }
}

function stepLabel(step: string | null | undefined): string {
  if (!step) return '-';
  return String(step).replace(/([A-Z])/g, ' $1').trim();
}

const OUTCOME_OPTIONS = FAILURE_STATES.map(value => ({ value, label: stateLabel(value), dotClassName: SESSION_OUTCOME_COLORS[value] }));

/**
 * Failure Detail report (Reports feature): every failed, expired, or not-authorized session
 * over a date range, with its failed step and error detail, for diagnosing recurring issues.
 * Every session of the period is loaded, because the failure rate needs the successful ones too.
 */
export default function ReportFailureDetailPage(): React.ReactElement {
  const { locations } = useUserPreferences();
  const [range, setRange] = useState<DateRange>(() => resolvePreset('last30')!);
  const [locationIds, setLocationIds] = useState<string[]>([]);
  const [outcomes, setOutcomes] = useState<string[]>([]);
  const [attempt, setAttempt] = useState(0);
  const [sort, toggleSort] = useSort<'state' | 'device' | 'location' | 'step' | 'error' | 'finished'>({ key: 'finished', dir: 'desc' });
  const { from, to } = range;
  const { records, failed } = useSessionHistory(range, attempt);

  const locationOptions = useMemo(() => locationFilterOptions(locations, records ?? []), [locations, records]);
  const sessions = useMemo(() => recordsForLocations(records ?? [], locationIds), [records, locationIds]);
  const failures = useMemo(() => sessions.filter(r => FAILURE_STATES.includes(r.finalState)), [sessions]);
  const counts = useMemo(() => countOutcomes(failures), [failures]);
  const devices = useMemo(() => {
    const perDevice = new Map<string, number>();
    for (const r of failures) perDevice.set(r.deviceSerialNumber, (perDevice.get(r.deviceSerialNumber) ?? 0) + 1);
    return { affected: perDevice.size, repeated: [...perDevice.values()].filter(n => n > 1).length };
  }, [failures]);
  const rate = share(failures.length, sessions.length);

  const sorted = useMemo(() => sortRows(
    failures.filter(r => outcomes.length === 0 || outcomes.includes(r.finalState)),
    sort,
    {
      state:    (r: SessionHistoryRecord) => stateLabel(r.finalState),
      device:   (r: SessionHistoryRecord) => r.deviceSerialNumber,
      location: (r: SessionHistoryRecord) => r.locationName ?? '',
      step:     (r: SessionHistoryRecord) => stepLabel(r.failedStepName),
      error:    (r: SessionHistoryRecord) => r.errorDetail ?? '',
      finished: (r: SessionHistoryRecord) => new Date(r.terminalAt).getTime(),
    },
  ), [failures, outcomes, sort]);

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
    const csv = toCsv(sorted, [
      { header: 'Session ID', accessor: r => r.sessionId },
      { header: 'Final State', accessor: r => stateLabel(r.finalState) },
      { header: 'Device Serial Number', accessor: r => r.deviceSerialNumber },
      { header: 'Manufacturer', accessor: r => r.deviceManufacturer },
      { header: 'Model', accessor: r => r.deviceModel },
      { header: 'Location', accessor: r => r.locationName ?? '' },
      { header: 'Failed Step', accessor: r => stepLabel(r.failedStepName) },
      { header: 'Error Detail', accessor: r => r.errorDetail ?? '' },
      { header: 'Started At', accessor: r => r.createdAt },
      { header: 'Finished At', accessor: r => r.terminalAt },
    ]);
    downloadBlob(`failure-detail-${from}-to-${to}.csv`, csv);
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
          id="failure-detail-range"
          aria-label="Date range"
          value={range}
          onValueChange={setRange}
          wrapperClassName="w-44"
        />
        <MultiSelect
          id="failure-detail-location"
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
          id="failure-detail-outcome"
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
          title="Could not load the failure detail"
          detail="The session history did not respond. Try again in a moment."
          onRetry={() => setAttempt(value => value + 1)}
        />
      ) : (
        <>
          <ReportSummary
            loading={records === null}
            total={failures.length}
            noun={['failure', 'failures']}
            parts={[
              { key: 'failed', label: 'Failed', count: counts.failed, colorClassName: SUMMARY_COLORS.negative },
              { key: 'expired', label: 'Expired', count: counts.expired, colorClassName: SUMMARY_COLORS.muted },
              { key: 'notAuthorized', label: 'Not authorized', count: counts.notAuthorized, colorClassName: SUMMARY_COLORS.warning },
            ]}
            emptyText={locationIds.length > 0
              ? 'No failed, expired or not authorized sessions in this period at the chosen locations.'
              : 'No failed, expired or not authorized sessions in this period.'}
            figures={[
              {
                label: 'Failure rate',
                value: rate === null ? null : `${rate}%`,
                detail: sessions.length === 0 ? 'No sessions in this period' : `${failures.length} of ${plural(sessions.length, 'session', 'sessions')}`,
              },
              {
                label: 'Devices affected',
                value: sessions.length === 0 ? null : String(devices.affected),
                detail: sessions.length === 0 ? 'No sessions in this period'
                  : failures.length === 0 ? 'No failures'
                  : devices.repeated === 0 ? 'None failed more than once' : `${devices.repeated} failed more than once`,
              },
            ]}
          />

          <div className="rounded-md border border-border overflow-hidden">
            <Table>
              <TableHeader>
                <TableRow className="hover:bg-transparent">
                  <SortableHead label="State" sortKey="state" sort={sort} onSort={toggleSort} />
                  <SortableHead label="Device" sortKey="device" sort={sort} onSort={toggleSort} />
                  <SortableHead label="Location" sortKey="location" sort={sort} onSort={toggleSort} />
                  <SortableHead label="Failed step" sortKey="step" sort={sort} onSort={toggleSort} />
                  <SortableHead label="Error detail" sortKey="error" sort={sort} onSort={toggleSort} />
                  <SortableHead label="Finished" sortKey="finished" sort={sort} onSort={toggleSort} />
                </TableRow>
              </TableHeader>
              <TableBody>
                {records === null && <TableSkeletonRows columns={6} rows={4} />}
                {records !== null && sorted.length === 0 && (
                  <TableRow>
                    <TableCell colSpan={6}>
                      {failures.length === 0 ? (
                        <EmptyState icon={AlertOctagon} title="No failures in this period."
                          description="Failed, expired, and not-authorized sessions will appear here as they occur." />
                      ) : (
                        <EmptyState icon={AlertOctagon} title="No failures match these filters" />
                      )}
                    </TableCell>
                  </TableRow>
                )}
                {sorted.map(r => (
                  <TableRow key={r.sessionId}>
                    <TableCell><Badge variant={stateBadgeVariant(r.finalState)} dot>{stateLabel(r.finalState)}</Badge></TableCell>
                    <TableCell>
                      <CopyableId value={r.deviceSerialNumber} label="device serial number" className="font-mono text-sm font-medium text-foreground" />
                      <div className="text-xs text-muted-foreground">{r.deviceManufacturer} {r.deviceModel}</div>
                    </TableCell>
                    <TableCell>{r.locationName ?? 'Unassigned'}</TableCell>
                    <TableCell>{stepLabel(r.failedStepName)}</TableCell>
                    <TableCell className="max-w-md truncate" title={r.errorDetail ?? undefined}>{r.errorDetail ?? '-'}</TableCell>
                    <TableCell>{formatDateTime(r.terminalAt)}</TableCell>
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
