import React, { useEffect, useState } from 'react';
import { CircleCheck, CircleDashed, CircleX, Clock, LoaderCircle, RefreshCw, TriangleAlert, X } from 'lucide-react';
import { Button } from '../ui/button.tsx';
import { Card, CardContent } from '../ui/card.tsx';
import { Skeleton } from '../ui/skeleton.tsx';
import { Table, TableBody, TableCell, TableRow } from '../ui/table.tsx';
import { cn, formatTimestamp } from '../../lib/utils.ts';
import {
  RESULT_LABELS, TRIGGER_LABELS, countsSummary, durationLabel, fetchSyncRunProgress, formatSize, onboardingSummary, vendorLabel,
  type DriverSyncRun, type DriverSyncRunItem, type DriverSyncRunProgress,
} from '../../lib/drivers.ts';
import { SyncOutcome } from './SyncOutcome.tsx';

/** How often a running sync's progress is read again. */
export const PROGRESS_POLL_MS = 5_000;

/** A run that ends sooner than this after the card appears keeps its stages on screen until then, so they can be seen. */
export const MIN_PROGRESS_VISIBLE_MS = 3_000;

type StageState = 'done' | 'active' | 'waiting' | 'warning';

const STAGE_ICONS: Record<StageState, React.ReactElement> = {
  done: <CircleCheck className="h-4 w-4 shrink-0 text-emerald-600 dark:text-emerald-400" aria-hidden="true" />,
  active: <LoaderCircle className="h-4 w-4 shrink-0 animate-spin text-blue-600 dark:text-blue-400" aria-hidden="true" />,
  waiting: <CircleDashed className="h-4 w-4 shrink-0 text-muted-foreground" aria-hidden="true" />,
  warning: <TriangleAlert className="h-4 w-4 shrink-0 text-amber-600 dark:text-amber-400" aria-hidden="true" />,
};

interface Stage {
  title: string;
  state: StageState;
  text: string;
  done: number;
  total: number;
}

function StageTile({ stage }: { stage: Stage }): React.ReactElement {
  const percent = stage.total === 0 ? 0 : Math.min(100, Math.round((stage.done / stage.total) * 100));
  // The bar starts empty and grows to its value, so a stage that finishes at once still visibly fills.
  const [painted, setPainted] = useState(false);
  useEffect(() => {
    const timer = window.setTimeout(() => setPainted(true), 0);
    return () => window.clearTimeout(timer);
  }, []);
  const width = !painted ? 0 : stage.state === 'done' || stage.state === 'warning' ? 100 : percent;
  return (
    <div className={cn('rounded-lg border border-border bg-background px-4 py-3', stage.state === 'waiting' && 'opacity-60')}>
      <div className="flex items-center gap-2 text-sm font-medium">{STAGE_ICONS[stage.state]}{stage.title}</div>
      <p className="mt-1 truncate text-sm text-muted-foreground tabular-nums" title={stage.text}>{stage.text}</p>
      <div
        role="progressbar"
        aria-label={stage.title}
        aria-valuemin={0}
        aria-valuemax={stage.total}
        aria-valuenow={stage.done}
        aria-valuetext={stage.text}
        className="mt-2 h-2 overflow-hidden rounded-full bg-muted"
      >
        <div className="h-full rounded-full bg-primary transition-[width] duration-700 ease-out" style={{ width: `${width}%` }} />
      </div>
    </div>
  );
}

function plural(count: number, one: string, many: string): string {
  return `${count} ${count === 1 ? one : many}`;
}

/** The stages of a run as tiles, each with its own count. Vendor catalogs only for a sync, which refreshes the model lists. */
function syncStages(progress: DriverSyncRunProgress): Stage[] {
  const { run, catalogs, models, components, driverSets } = progress;
  const catalogsDone = catalogs.done >= catalogs.total;
  const modelsDone = catalogsDone && models.done >= models.total;
  const setsDone = modelsDone && driverSets.done >= driverSets.total;
  const failed = run.vendors.filter(v => !v.processed).map(v => vendorLabel(v.vendor));
  const newModels = run.vendors.reduce((sum, v) => sum + v.newModels, 0);

  const catalogText = [`${catalogs.done} of ${catalogs.total} read`];
  if (newModels > 0) catalogText.push(plural(newModels, 'new model', 'new models'));
  if (failed.length > 0) catalogText.push(`${failed.join(' and ')} failed`);

  const stages: Stage[] = [];
  const refreshesCatalogs = run.trigger === 'Scheduled' || run.trigger === 'Manual';
  if (refreshesCatalogs) {
    stages.push({ title: 'Vendor catalogs', state: catalogsDone ? (failed.length > 0 ? 'warning' : 'done') : 'active', text: catalogText.join(', '), ...catalogs });
  }
  stages.push(models.total === 0 && !catalogsDone
    ? { title: 'Models', state: 'waiting', text: refreshesCatalogs ? 'Waiting for the catalogs' : 'Waiting to start', ...models }
    : { title: 'Models', state: modelsDone ? 'done' : 'active', text: `${models.done} of ${models.total} checked`, ...models });

  const downloaded = run.downloadedComponents ?? 0;
  const bytes = run.downloadedBytes ?? 0;
  stages.push(components.total === 0
    ? { title: 'Components', state: setsDone ? 'done' : 'waiting', text: setsDone ? 'Nothing new to download' : 'Waiting for the driver sets', ...components }
    : {
      title: 'Components',
      state: setsDone && components.done >= components.total ? 'done' : 'active',
      text: `${downloaded} of ${components.total} downloaded${bytes > 0 ? `, ${formatSize(bytes)}` : ''}`,
      ...components,
    });

  const newSets = run.newSets ?? 0;
  stages.push(driverSets.total === 0
    ? { title: 'Driver sets', state: modelsDone ? 'done' : 'waiting', text: modelsDone ? 'Nothing to build' : 'Waiting for the models', ...driverSets }
    : { title: 'Driver sets', state: setsDone ? 'done' : 'active', text: `${driverSets.done} of ${driverSets.total} checked${newSets > 0 ? `, ${newSets} new` : ''}`, ...driverSets });
  return stages;
}

function finishedTitle(run: DriverSyncRun): string {
  if (run.trigger === 'Onboarding') return run.modelName ? `${run.modelName} onboarded` : 'Onboarding finished';
  return run.trigger === 'Check' || run.trigger === 'ModelCheck' ? 'Check finished' : 'Sync finished';
}

/** What a finished run changed and downloaded; a run with nothing new says what it checked, so it does not look like it skipped the work. */
function finishedSummary({ run, models }: DriverSyncRunProgress): string {
  const changes = countsSummary(run.counts);
  if (!changes && run.trigger === 'Onboarding') return onboardingSummary(run);
  const downloaded = run.downloadedComponents ?? 0;
  const bytes = run.downloadedBytes ?? 0;
  const download = downloaded > 0 ? `${plural(downloaded, 'component', 'components')} downloaded${bytes > 0 ? `, ${formatSize(bytes)}` : ''}` : 'nothing downloaded';
  return changes
    ? `${changes}. ${download.charAt(0).toUpperCase()}${download.slice(1)}`
    : `${plural(models.done, 'model', 'models')} checked, no newer versions, ${download}`;
}

function runningTitle(run: DriverSyncRun): string {
  if (run.trigger === 'Onboarding') return run.modelName ? `Onboarding ${run.modelName}` : 'Onboarding';
  if (run.trigger === 'ModelCheck') return run.modelName ? `Checking ${run.modelName}` : 'Checking for updates';
  return run.trigger === 'Check' ? 'Checking for updates' : 'Sync running';
}

function startedByText(run: DriverSyncRun): string {
  return run.trigger === 'Scheduled' || !run.startedBy ? TRIGGER_LABELS[run.trigger] : `${TRIGGER_LABELS[run.trigger]} by ${run.startedBy}`;
}

function change(item: DriverSyncRunItem): string {
  if (item.oldVersion && item.newVersion) return `${item.oldVersion} to ${item.newVersion}`;
  return item.newVersion ?? item.oldVersion ?? '';
}

function Header({ icon, iconClassName, title, meta, action }: { icon: React.ReactNode; iconClassName: string; title: string; meta: string; action?: React.ReactNode }): React.ReactElement {
  return (
    <div className="flex items-center gap-3">
      <span className={cn('flex h-9 w-9 shrink-0 items-center justify-center rounded-lg', iconClassName)}>{icon}</span>
      <div className="min-w-0 flex-1">
        <h3 className="text-base font-semibold">{title}</h3>
        <p className="text-sm text-muted-foreground">{meta}</p>
      </div>
      {action}
    </div>
  );
}

const FINISHED_ICON = {
  Completed: { icon: <CircleCheck className="h-5 w-5" aria-hidden="true" />, className: 'bg-emerald-500/15 text-emerald-600 dark:text-emerald-400' },
  PartlyFailed: { icon: <TriangleAlert className="h-5 w-5" aria-hidden="true" />, className: 'bg-amber-500/15 text-amber-600 dark:text-amber-400' },
  Failed: { icon: <CircleX className="h-5 w-5" aria-hidden="true" />, className: 'bg-red-500/15 text-red-600 dark:text-red-400' },
} as const;

/**
 * The Sync running card at the top of the Sync tab: while a run is running, each stage with its
 * own count, what it found so far and its latest changes; once it ends, its result until dismissed.
 */
export function SyncProgressCard({ runId, onFinished, onDismiss }: { runId: string; onFinished: () => void; onDismiss: () => void }): React.ReactElement {
  const [progress, setProgress] = useState<DriverSyncRunProgress | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [now, setNow] = useState(() => Date.now());
  const [shownAt] = useState(() => Date.now());
  const [resultShown, setResultShown] = useState(false);

  useEffect(() => {
    let cancelled = false;
    let timer: number | undefined;
    const read = async (): Promise<void> => {
      try {
        const next = await fetchSyncRunProgress(runId);
        if (cancelled) return;
        setProgress(next);
        setError(null);
        setNow(Date.now());
        if (next.run.result === 'Running') {
          timer = window.setTimeout(() => void read(), PROGRESS_POLL_MS);
          return;
        }
        const hold = MIN_PROGRESS_VISIBLE_MS - (Date.now() - shownAt);
        const finish = () => { setResultShown(true); onFinished(); };
        if (hold > 0) timer = window.setTimeout(finish, hold);
        else finish();
      } catch (err) {
        if (cancelled) return;
        setError(err instanceof Error ? err.message : 'Could not load the progress of the sync.');
        timer = window.setTimeout(() => void read(), PROGRESS_POLL_MS);
      }
    };
    void read();
    return () => {
      cancelled = true;
      window.clearTimeout(timer);
    };
  }, [runId, onFinished, shownAt]);

  if (!progress) {
    return (
      <Card>
        <CardContent className="p-6">
          {error
            ? <p role="alert" className="text-sm text-destructive">{`${error} Trying again.`}</p>
            : <Skeleton className="h-24 w-full" />}
        </CardContent>
      </Card>
    );
  }

  const { run } = progress;
  const startedAt = Date.parse(run.startedAt);

  if (run.result !== 'Running' && resultShown) {
    const finishedAt = run.finishedAt ? Date.parse(run.finishedAt) : now;
    const look = FINISHED_ICON[run.result];
    return (
      <Card>
        <CardContent className="p-6">
          <Header
            icon={look.icon}
            iconClassName={look.className}
            title={finishedTitle(run)}
            meta={`${RESULT_LABELS[run.result]} at ${formatTimestamp(run.finishedAt ?? run.startedAt)}, after ${durationLabel(finishedAt - startedAt)}: ${finishedSummary(progress)}.`}
            action={<Button variant="ghost" size="icon" aria-label="Dismiss" onClick={onDismiss}><X /></Button>}
          />
        </CardContent>
      </Card>
    );
  }

  const stages = syncStages(progress);
  // While a quick run's result is held back its stages stay on screen, filled, rather than collapsing into Finishing up.
  const finishing = run.result === 'Running' && stages.every(s => s.state === 'done' || s.state === 'warning');
  const found = countsSummary(progress.found);
  const failedVendors = run.vendors.filter(v => !v.processed);

  return (
    <Card>
      <CardContent className="space-y-4 p-6">
        <Header
          icon={<RefreshCw className="h-5 w-5" aria-hidden="true" />}
          iconClassName="bg-blue-500/15 text-blue-600 dark:text-blue-400"
          title={runningTitle(run)}
          meta={`${startedByText(run)}, started ${formatTimestamp(run.startedAt)}, running for ${durationLabel(now - startedAt)}.`}
        />
        {finishing ? (
          <div className="flex items-center gap-3">
            <div className="h-2 flex-1 overflow-hidden rounded-full bg-muted" aria-hidden="true">
              <div className="h-full w-full animate-pulse rounded-full bg-primary/60" />
            </div>
            <span className="text-sm text-muted-foreground">Finishing up</span>
          </div>
        ) : (
          <div className={cn('grid gap-3', stages.length === 4 ? 'sm:grid-cols-2 lg:grid-cols-4' : 'sm:grid-cols-3')}>
            {stages.map(stage => <StageTile key={stage.title} stage={stage} />)}
          </div>
        )}
        {failedVendors.map(v => (
          <p key={v.vendor} className="flex items-center gap-2 text-sm">
            <CircleX className="h-4 w-4 shrink-0 text-destructive" aria-hidden="true" />
            {`${vendorLabel(v.vendor)}: ${v.failureReason ?? 'The catalog could not be read.'}`}
          </p>
        ))}
        <p className="text-sm">
          <span className="text-muted-foreground">Found so far: </span>
          <span className="font-medium">{found || 'nothing yet'}</span>
        </p>
        {progress.latest.length > 0 && (
          <div className="overflow-hidden rounded-md border border-border">
            <Table className="table-fixed">
              <TableBody>
                {progress.latest.map((item, i) => (
                  <TableRow key={`${item.at ?? ''}-${i}`}>
                    <TableCell className="w-[72px] text-xs text-muted-foreground tabular-nums">{item.at ? formatTimestamp(item.at).slice(11) : ''}</TableCell>
                    <TableCell className="w-[26%] truncate" title={item.modelName ?? undefined}>
                      {item.modelName ?? (item.componentName ? <span className="text-muted-foreground">Every model that uses it</span> : null)}
                    </TableCell>
                    <TableCell className="truncate" title={item.componentName ?? item.reason ?? undefined}>{item.componentName ?? <span className="text-muted-foreground">{item.reason}</span>}</TableCell>
                    <TableCell className="w-[22%] truncate">{change(item)}</TableCell>
                    <TableCell className="w-[20%]"><SyncOutcome outcome={item.outcome} reason={item.outcome === 'Failed' ? item.reason : null} /></TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </div>
        )}
        {(progress.queued ?? []).length > 0 && (
          <div className="space-y-2">
            <p className="text-sm font-medium">Up next</p>
            <ul className="divide-y divide-border overflow-hidden rounded-md border border-border">
              {(progress.queued ?? []).map(queued => (
                <li key={queued.modelId} className="flex items-center gap-3 px-3 py-2 text-sm">
                  <Clock className="h-4 w-4 shrink-0 text-muted-foreground" aria-hidden="true" />
                  <span className="min-w-0 flex-1 truncate" title={queued.modelName}>{`Onboarding ${queued.modelName}`}</span>
                  <span className="shrink-0 text-xs text-muted-foreground">{`Queued by ${queued.queuedBy} at ${formatTimestamp(queued.queuedAt).slice(11)}`}</span>
                </li>
              ))}
            </ul>
          </div>
        )}
        {error && <p role="alert" className="text-sm text-destructive">{`${error} Trying again.`}</p>}
      </CardContent>
    </Card>
  );
}
