import React, { useCallback, useEffect, useState } from 'react';
import { Clock, Rocket, RefreshCw } from 'lucide-react';
import { ConfirmImpactDialog } from '../ConfirmImpactDialog.tsx';
import { Button } from '../ui/button.tsx';
import { EmptyState } from '../ui/empty-state.tsx';
import { Input } from '../ui/input.tsx';
import { Select } from '../ui/select.tsx';
import { Skeleton } from '../ui/skeleton.tsx';
import { ToggleGroup } from '../ui/toggle-group.tsx';
import { Tooltip } from '../ui/tooltip.tsx';
import { useAuth } from '../../context/authContext.tsx';
import { useToast } from '../../context/toastContext.tsx';
import { useRerenderAt } from '../../hooks/useRerenderAt.ts';
import { formatTimestamp } from '../../lib/utils.ts';
import {
  MONDAY_TO_FRIDAY, WEEKDAYS, availableAgainText, browserTimeZone, fetchSyncRuns, fetchSyncSettings, fetchVendors, nextSyncLine, saveSyncSettings, syncNow,
  type DriverSyncRun, type DriverSyncSchedule, type DriverSyncSettings, type DriverSyncSettingsInput, type DriverVendorRow,
} from '../../lib/drivers.ts';
import { SyncHistory } from './SyncHistory.tsx';
import { DriverCard, DriverCardRow, DriverCardRows } from './DriverCard.tsx';
import { SyncProgressCard } from './SyncProgressCard.tsx';
import { TimeZonePicker } from './TimeZonePicker.tsx';
import { VendorsCard } from './VendorsCard.tsx';

const SCHEDULES: { value: DriverSyncSchedule; label: string }[] = [
  { value: 'Daily', label: 'Every day' },
  { value: 'SelectedDays', label: 'Selected days' },
  { value: 'Manual', label: 'Manually only' },
];

const REPEATS = [1, 2, 3, 4].map(n => ({ value: String(n), label: n === 1 ? 'Every week' : `Every ${n} weeks` }));

const DAY_ERROR = 'Choose at least one day. The last saved days still apply.';

const SYNC_DESCRIPTION = 'Copies new drivers from supported vendors for your onboarded models to your storage account, so devices never download from the vendor during a deployment.';

const TIME_PATTERN = /^([01]\d|2[0-3]):[0-5]\d$/;

const NEW_COMPONENT_OPTIONS = [
  { value: 'wait', label: 'Wait for approval' },
  { value: 'auto', label: 'Use automatically' },
];

const NEWER_VERSION_OPTIONS = [
  { value: 'auto', label: 'Use automatically' },
  { value: 'testing', label: 'Wait for testing' },
];

/** How often the history is read again while a sync runs. */
export const SYNC_POLL_MS = 15_000;

/** Sync tab: schedule, Sync now, approval setting, vendors and the run history. */
export function SyncTab(): React.ReactElement {
  const { canManageDrivers } = useAuth();
  const { notify } = useToast();
  const [settings, setSettings] = useState<DriverSyncSettings | null>(null);
  const [vendors, setVendors] = useState<DriverVendorRow[]>([]);
  const [runs, setRuns] = useState<DriverSyncRun[] | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [days, setDays] = useState(MONDAY_TO_FRIDAY);
  const [time, setTime] = useState('02:00');
  const [saving, setSaving] = useState(false);
  const [starting, setStarting] = useState(false);
  const [confirmingRelease, setConfirmingRelease] = useState(false);

  const applyLoaded = useCallback((loaded: DriverSyncSettings) => {
    setSettings(current => ({ ...loaded, timeZones: loaded.timeZones.length > 0 ? loaded.timeZones : current?.timeZones ?? [] }));
    setDays(loaded.weekdays || MONDAY_TO_FRIDAY);
    setTime(loaded.localTime);
  }, []);

  const save = useCallback(async (next: DriverSyncSettingsInput, quiet = false, description?: string) => {
    setSaving(true);
    try {
      applyLoaded(await saveSyncSettings(next));
      if (!quiet) notify(description ? { status: 'success', title: 'Sync settings saved.', description } : { status: 'success', title: 'Sync settings saved.' });
    } catch (err) {
      notify({ status: 'error', title: 'Could not save the sync settings', description: err instanceof Error ? err.message : undefined });
    } finally {
      setSaving(false);
    }
  }, [applyLoaded, notify]);

  const load = useCallback(async () => {
    setLoadError(null);
    try {
      const [loaded, vendorList, runList] = await Promise.all([fetchSyncSettings(), fetchVendors(), fetchSyncRuns()]);
      applyLoaded(loaded);
      setVendors(vendorList);
      setRuns(runList);

      // FR-032: until the schedule is first saved, it runs in the browser's time zone.
      const browser = browserTimeZone(loaded.timeZones);
      if (canManageDrivers && loaded.changedAt === null && browser && browser.ianaId !== loaded.timeZone) {
        await save({ ...loaded, timeZone: browser.ianaId }, true);
      }
    } catch (err) {
      setLoadError(err instanceof Error ? err.message : 'Could not load the sync settings.');
    }
  }, [applyLoaded, canManageDrivers, save]);

  useEffect(() => { void load(); }, [load]);

  // A running sync finishes in the background; its result, changes and new models appear without a reload.
  const running = runs?.some(r => r.result === 'Running') ?? false;
  useEffect(() => {
    if (!running) return undefined;
    const timer = window.setInterval(() => {
      fetchSyncRuns().then(setRuns).catch(() => undefined);
    }, SYNC_POLL_MS);
    return () => window.clearInterval(timer);
  }, [running]);

  // The Sync running card follows the running run, and stays on its result until dismissed.
  const [shownRunId, setShownRunId] = useState<string | null>(null);
  const [dismissedRunId, setDismissedRunId] = useState<string | null>(null);
  const runningRunId = runs?.find(r => r.result === 'Running')?.runId ?? null;
  if (runningRunId && runningRunId !== shownRunId && runningRunId !== dismissedRunId) {
    setShownRunId(runningRunId);
  }
  const runFinished = useCallback(() => {
    fetchSyncRuns().then(setRuns).catch(() => undefined);
    // When Sync now is available again counts from the run's start, so it is read again once the run ends.
    fetchSyncSettings()
      .then(loaded => setSettings(current => (current ? { ...current, syncNowAvailableAt: loaded.syncNowAvailableAt, lastRunStartedAt: loaded.lastRunStartedAt, nextRunAt: loaded.nextRunAt } : current)))
      .catch(() => undefined);
  }, []);
  const dismissRun = useCallback(() => {
    setDismissedRunId(shownRunId);
    setShownRunId(null);
  }, [shownRunId]);
  useRerenderAt(settings?.syncNowAvailableAt);

  if (loadError) {
    return (
      <div className="rounded-md border border-border">
        <EmptyState icon={RefreshCw} title="Could not load the sync settings" description={loadError} action={<Button variant="outline" onClick={() => void load()}><RefreshCw />Try again</Button>} />
      </div>
    );
  }
  if (!settings) {
    return <div className="space-y-4"><Skeleton className="h-64 w-full" /><Skeleton className="h-32 w-full" /></div>;
  }

  const input: DriverSyncSettingsInput = {
    schedule: settings.schedule,
    weekdays: settings.weekdays,
    repeatWeeks: settings.repeatWeeks,
    localTime: settings.localTime,
    timeZone: settings.timeZone,
    waitForApprovalOfNewComponents: settings.waitForApprovalOfNewComponents,
    waitForTesting: settings.waitForTesting,
  };
  const readOnly = !canManageDrivers || saving;
  const manual = settings.schedule === 'Manual';
  const dayError = settings.schedule === 'SelectedDays' && days === 0;
  const nextLine = nextSyncLine(settings);
  const waiting = availableAgainText(settings.syncNowAvailableAt);
  const browser = browserTimeZone(settings.timeZones);

  const chooseSchedule = (schedule: DriverSyncSchedule) => {
    if (schedule === 'SelectedDays' && settings.schedule !== 'SelectedDays') {
      setDays(MONDAY_TO_FRIDAY);
      void save({ ...input, schedule, weekdays: MONDAY_TO_FRIDAY, repeatWeeks: 1 });
      return;
    }
    void save({ ...input, schedule });
  };

  const toggleDay = (bit: number) => {
    const next = days ^ bit;
    setDays(next);
    if (next !== 0) void save({ ...input, weekdays: next });
  };

  const changeTime = (value: string) => {
    setTime(value);
    if (TIME_PATTERN.test(value) && value !== settings.localTime) void save({ ...input, localTime: value });
  };

  // Using newer versions automatically again releases every set in testing, so that is confirmed first.
  const inTesting = settings.setsInTesting ?? 0;
  const chooseNewerVersions = async (value: string) => {
    const waitForTesting = value === 'testing';
    if (!waitForTesting && settings.waitForTesting) {
      // Sets go into testing and are released in the background, so the count is read again.
      const fresh = await fetchSyncSettings().then(s => s.setsInTesting ?? 0, () => inTesting);
      setSettings(current => (current ? { ...current, setsInTesting: fresh } : current));
      if (fresh > 0) {
        setConfirmingRelease(true);
        return;
      }
    }
    void save({ ...input, weekdays: days || settings.weekdays, waitForTesting });
  };

  const releaseAndUseAutomatically = async () => {
    await save({ ...input, weekdays: days || settings.weekdays, waitForTesting: false }, false, `${inTesting === 1 ? '1 driver set' : `${inTesting} driver sets`} released from testing.`);
    setConfirmingRelease(false);
  };

  const startSync = async () => {
    setStarting(true);
    try {
      const started = await syncNow();
      setShownRunId(started.runId);
      notify({ status: 'success', title: 'Sync started.' });
      await load();
    } catch (err) {
      notify({ status: 'error', title: 'Could not start the sync', description: err instanceof Error ? err.message : undefined });
    } finally {
      setStarting(false);
    }
  };

  return (
    <div className="space-y-6">
      {shownRunId && <SyncProgressCard key={shownRunId} runId={shownRunId} onFinished={runFinished} onDismiss={dismissRun} />}
      <DriverCard
        title="Sync"
        divided={false}
        description={`${SYNC_DESCRIPTION}${manual ? ' Nothing runs on a schedule: new drivers wait until someone selects Sync now.' : ''}`}
        aside={(
          <div className="flex flex-col gap-2">
            <div className="flex gap-2">
              <Select value={settings.schedule} onValueChange={v => chooseSchedule(v as DriverSyncSchedule)} options={SCHEDULES} aria-label="Schedule" disabled={readOnly} wrapperClassName="w-[170px]" />
              {!manual && (
                <>
                  <div className="relative w-24">
                    <Clock className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground" aria-hidden="true" />
                    <Input
                      value={time}
                      onChange={e => changeTime(e.target.value)}
                      onBlur={() => { if (!TIME_PATTERN.test(time)) setTime(settings.localTime); }}
                      aria-label="Time"
                      inputMode="numeric"
                      placeholder="HH:MM"
                      maxLength={5}
                      disabled={readOnly}
                      className="pl-9 tabular-nums"
                    />
                  </div>
                  <TimeZonePicker zones={settings.timeZones} value={settings.timeZone} browser={browser} disabled={readOnly} onChange={zone => void save({ ...input, timeZone: zone })} />
                </>
              )}
            </div>
            {settings.schedule === 'SelectedDays' && (
              <div className="flex items-center gap-2">
                <Select value={String(settings.repeatWeeks)} onValueChange={v => void save({ ...input, weekdays: days || settings.weekdays, repeatWeeks: Number(v) })} options={REPEATS} aria-label="Repeat" disabled={readOnly} wrapperClassName="w-[170px]" />
                <ToggleGroup
                  aria-label="Days"
                  items={WEEKDAYS.map(day => ({ value: day.bit, label: day.short, ariaLabel: day.long }))}
                  isOn={bit => (days & bit) !== 0}
                  onToggle={toggleDay}
                  disabled={readOnly}
                  invalid={dayError}
                  className="flex-1"
                />
              </div>
            )}
            {/* Aligned under the time field; the day error takes the hint's place so nothing moves. */}
            {!manual && (dayError ? (
              <p className="ml-[178px] text-sm text-destructive" role="alert">{DAY_ERROR}</p>
            ) : (
              <p className="ml-[178px] min-h-4 text-xs text-muted-foreground" aria-live="polite">{nextLine}</p>
            ))}
          </div>
        )}
      >
        <div className="px-5">
          <div className="border-t border-border/70">
            <DriverCardRow
              label="Sync now"
              help={`Runs the same sync as the schedule: refreshes the model list of every enabled vendor and checks their onboarded models for component updates.${settings.lastRunStartedAt ? ` Last sync started ${formatTimestamp(settings.lastRunStartedAt)}.` : ''}`}
            >
              {canManageDrivers && (
                <Tooltip content={running ? 'A sync is running.' : waiting ?? 'Starts a sync now.'}>
                  <span>
                    <Button variant="outline" onClick={() => void startSync()} disabled={running || waiting !== null} loading={starting}><RefreshCw />Sync</Button>
                  </span>
                </Tooltip>
              )}
            </DriverCardRow>
          </div>
        </div>
        <div className="border-t border-border">
          <DriverCardRows>
            <DriverCardRow
              label="Newer versions"
              help="The vendor replaces a component the model already has with a newer release, for example Intel WLAN 24.60.0.1 to 24.60.0.3. The new version keeps the same action. Wait for testing holds every new driver set, including a new model's first, until an Operator releases it on the model page. Until then only devices an Operator chooses it for at imaging start get it. Switching back to Use automatically releases every set in testing."
            >
              <Select
                value={settings.waitForTesting ? 'testing' : 'auto'}
                onValueChange={v => void chooseNewerVersions(v)}
                options={NEWER_VERSION_OPTIONS}
                aria-label="Newer versions"
                disabled={readOnly}
                wrapperClassName="w-[240px]"
              />
            </DriverCardRow>
            <DriverCardRow
              label="New components"
              help="The vendor adds a component the model did not have, such as a service or an app. Rare, and the moment someone should look. Your rules still apply first."
            >
              <Select
                value={settings.waitForApprovalOfNewComponents ? 'wait' : 'auto'}
                onValueChange={v => void save({ ...input, weekdays: days || settings.weekdays, waitForApprovalOfNewComponents: v === 'wait' })}
                options={NEW_COMPONENT_OPTIONS}
                aria-label="New components"
                disabled={readOnly}
                wrapperClassName="w-[240px]"
              />
            </DriverCardRow>
          </DriverCardRows>
        </div>
      </DriverCard>

      <VendorsCard
        vendors={vendors}
        readOnly={!canManageDrivers}
        onChanged={row => setVendors(list => list.map(v => (v.vendor === row.vendor ? row : v)))}
        onError={message => notify({ status: 'error', title: 'Could not change the vendor', description: message })}
      />

      <SyncHistory runs={runs} />

      {confirmingRelease && (
        <ConfirmImpactDialog
          icon={Rocket}
          titleId="release-testing-title"
          copy={{
            confirmTitle: 'Use newer versions automatically?',
            impact: `${inTesting === 1 ? 'The driver set in testing is released' : `All ${inTesting} driver sets in testing are released`}: devices imaged from now on get ${inTesting === 1 ? 'it' : 'them'} instead of the current set. New driver sets no longer wait in testing.`,
            confirmLabel: 'Release and use automatically',
            destructive: false,
          }}
          busy={saving}
          onCancel={() => setConfirmingRelease(false)}
          onConfirm={() => void releaseAndUseAutomatically()}
        />
      )}
    </div>
  );
}
