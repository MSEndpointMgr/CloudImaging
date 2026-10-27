import React, { useCallback, useEffect, useState } from 'react';
import { RefreshCw } from 'lucide-react';
import { Button } from '../ui/button.tsx';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '../ui/card.tsx';
import { EmptyState } from '../ui/empty-state.tsx';
import { Input } from '../ui/input.tsx';
import { Select } from '../ui/select.tsx';
import { Skeleton } from '../ui/skeleton.tsx';
import { Switch } from '../ui/switch.tsx';
import { Tooltip } from '../ui/tooltip.tsx';
import { useAuth } from '../../context/authContext.tsx';
import { useToast } from '../../context/toastContext.tsx';
import { cn, formatTimestamp } from '../../lib/utils.ts';
import {
  MONDAY_TO_FRIDAY, WEEKDAYS, availableAgainText, browserTimeZone, fetchSyncRuns, fetchSyncSettings, fetchVendors, nextSyncLine, saveSyncSettings, syncNow,
  type DriverSyncRun, type DriverSyncSchedule, type DriverSyncSettings, type DriverSyncSettingsInput, type DriverVendorRow,
} from '../../lib/drivers.ts';
import { SyncHistory } from './SyncHistory.tsx';
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

/** How often the history is read again while a sync runs. */
export const SYNC_POLL_MS = 15_000;

/** A setting: label and help on the left, the control vertically centred on the right. */
function SettingRow({ label, help, children }: { label: string; help?: React.ReactNode; children: React.ReactNode }): React.ReactElement {
  return (
    <div className="flex items-center gap-6 py-3">
      <div className="min-w-0 flex-1">
        <p className="text-sm font-medium">{label}</p>
        {help && <p className="text-sm text-muted-foreground">{help}</p>}
      </div>
      <div className="flex shrink-0 items-center gap-2">{children}</div>
    </div>
  );
}

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

  const applyLoaded = useCallback((loaded: DriverSyncSettings) => {
    setSettings(current => ({ ...loaded, timeZones: loaded.timeZones.length > 0 ? loaded.timeZones : current?.timeZones ?? [] }));
    setDays(loaded.weekdays || MONDAY_TO_FRIDAY);
    setTime(loaded.localTime);
  }, []);

  const save = useCallback(async (next: DriverSyncSettingsInput, quiet = false) => {
    setSaving(true);
    try {
      applyLoaded(await saveSyncSettings(next));
      if (!quiet) notify({ status: 'success', title: 'Sync settings saved.' });
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
  }, []);
  const dismissRun = useCallback(() => {
    setDismissedRunId(shownRunId);
    setShownRunId(null);
  }, [shownRunId]);

  if (loadError) {
    return <EmptyState icon={RefreshCw} title="Could not load the sync settings" description={loadError} action={<Button variant="outline" onClick={() => void load()}><RefreshCw />Try again</Button>} />;
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
  };
  const readOnly = !canManageDrivers || saving;
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
    if (/^([01]\d|2[0-3]):[0-5]\d$/.test(value) && value !== settings.localTime) void save({ ...input, localTime: value });
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
      <Card>
        <CardHeader>
          <CardTitle className="text-base">Sync</CardTitle>
          <CardDescription>Downloads new driver versions for onboarded models from the vendors.</CardDescription>
        </CardHeader>
        <CardContent className="space-y-0">
          <SettingRow label="Schedule">
            {settings.schedule === 'SelectedDays' && (
              <>
                <Select value={String(settings.repeatWeeks)} onValueChange={v => void save({ ...input, weekdays: days || settings.weekdays, repeatWeeks: Number(v) })} options={REPEATS} aria-label="Repeat" disabled={readOnly} wrapperClassName="w-[170px]" />
                <div className="flex gap-1" role="group" aria-label="Days">
                  {WEEKDAYS.map(day => {
                    const on = (days & day.bit) !== 0;
                    return (
                      <Button
                        key={day.bit}
                        variant={on ? 'secondary' : 'outline'}
                        aria-pressed={on}
                        aria-label={day.long}
                        disabled={readOnly}
                        onClick={() => toggleDay(day.bit)}
                      >
                        {day.short}
                      </Button>
                    );
                  })}
                </div>
              </>
            )}
            <Select value={settings.schedule} onValueChange={v => chooseSchedule(v as DriverSyncSchedule)} options={SCHEDULES} aria-label="Schedule" disabled={readOnly} wrapperClassName="w-[170px]" />
          </SettingRow>
          {settings.schedule !== 'Manual' && (
            <>
              <SettingRow label="Time">
                <Input type="time" value={time} onChange={e => changeTime(e.target.value)} aria-label="Time" disabled={readOnly} className="w-[120px]" />
              </SettingRow>
              <SettingRow label="Time zone">
                <TimeZonePicker zones={settings.timeZones} value={settings.timeZone} browser={browser} disabled={readOnly} onChange={zone => void save({ ...input, timeZone: zone })} />
              </SettingRow>
            </>
          )}
          {/* One line of fixed height, so the day error replaces the next-sync line without moving anything. */}
          <p className={cn('h-6 text-right text-sm', dayError ? 'text-destructive' : 'text-muted-foreground')} role={dayError ? 'alert' : undefined}>
            {dayError ? DAY_ERROR : nextLine ?? (settings.schedule === 'Manual' ? 'Nothing runs on a schedule: new drivers wait until someone selects Sync now.' : '')}
          </p>

          <div className="mx-4 border-t border-border" />
          <SettingRow
            label="Sync now"
            help={`Runs the same sync as the schedule: refreshes the model list of every enabled vendor and checks their onboarded models for component updates.${settings.lastRunStartedAt ? ` Last sync started ${formatTimestamp(settings.lastRunStartedAt)}.` : ''}`}
          >
            {canManageDrivers && (
              <Tooltip content={waiting ?? 'Starts a sync now.'}>
                <span>
                  <Button variant="outline" onClick={() => void startSync()} disabled={waiting !== null} loading={starting}><RefreshCw />Sync</Button>
                </span>
              </Tooltip>
            )}
          </SettingRow>

          <div className="-mx-6 border-t border-border" />
          <SettingRow label="Newer versions" help="A newer version of a component already in a driver set replaces it at the next sync.">
            <span className="text-sm">Use automatically</span>
          </SettingRow>
          <SettingRow label="New components" help="Components a model did not have before are held until someone approves them.">
            <Switch
              checked={settings.waitForApprovalOfNewComponents}
              onCheckedChange={on => void save({ ...input, weekdays: days || settings.weekdays, waitForApprovalOfNewComponents: on })}
              label="Wait for approval"
              disabled={readOnly}
            />
            <span className="text-sm">Wait for approval</span>
          </SettingRow>
        </CardContent>
      </Card>

      <VendorsCard
        vendors={vendors}
        readOnly={!canManageDrivers}
        onChanged={row => setVendors(list => list.map(v => (v.vendor === row.vendor ? row : v)))}
        onError={message => notify({ status: 'error', title: 'Could not change the vendor', description: message })}
      />

      <SyncHistory runs={runs} />
    </div>
  );
}
