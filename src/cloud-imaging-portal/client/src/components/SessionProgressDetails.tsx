import * as React from 'react';
import { AlertTriangle, Check, LoaderCircle, Minus, X } from 'lucide-react';
import { Badge } from './ui/badge.tsx';
import { Button } from './ui/button.tsx';
import { Tooltip } from './ui/tooltip.tsx';
import { cn, formatDateTime, formatDuration } from '../lib/utils.ts';
import {
  PIPELINE_STEPS,
  PIPELINE_STEP_SHORT_LABELS,
  type ImagingStepDetails,
} from '../lib/imagingProgress.ts';
import {
  ITEM_OUTCOME_LABELS,
  fetchStepItems,
  itemCountsSummary,
  type ImagingStepItem,
  type SetupProfilePin,
  type StepItemOutcome,
} from '../lib/setupProfiles.ts';

interface SessionProgressDetailsProps {
  overallPercent: number;
  currentStep: string | null;
  steps?: ImagingStepDetails[];
  /** Needed to load a step's items. */
  sessionId?: string;
  /** The setup profile the session pinned when imaging started. */
  setupProfile?: SetupProfilePin | null;
  /** The session has ended (completed, failed or abandoned). */
  terminal?: boolean;
}

type StepStatus = 'Completed' | 'Warning' | 'InProgress' | 'Failed' | 'Pending' | 'NotRun';

const PREPARE_SETUP = 'PrepareSetup';
const NOT_RUN_TOOLTIP = "This device's boot image is older than Setup Profiles, so the profile was not applied. Update the boot image.";

const OUTCOME_BADGES: Record<StepItemOutcome, 'success' | 'negative' | 'info' | 'muted'> = {
  Succeeded: 'success',
  Failed: 'negative',
  Queued: 'info',
  NotRun: 'muted',
};

interface ResolvedStep {
  name: string;
  label: string;
  shortLabel: string;
  status: StepStatus;
  percent?: number | null;
  startedAt?: string | null;
  completedAt?: string | null;
  errorDetail?: string | null;
  /** Null until the step starts, so pending steps render a dash rather than "0s". */
  durationMs: number | null;
  itemCounts: ImagingStepDetails['itemCounts'];
  itemsTruncated: boolean | null;
}

// A running step's elapsed time is wrong the moment it is painted. One shared ticker re-renders
// every mounted instance, mirroring `RelativeTime`, so expanding several rows costs one interval.
// Unlike that component this ticks every second, because the question here is "has it stalled".
// The clock lives in the store rather than being read during render, which would be impure.
const listeners = new Set<() => void>();
let ticker: ReturnType<typeof setInterval> | null = null;
let currentTime = Date.now();

function subscribe(onStoreChange: () => void): () => void {
  currentTime = Date.now();
  listeners.add(onStoreChange);
  ticker ??= setInterval(() => {
    currentTime = Date.now();
    listeners.forEach(listener => listener());
  }, 1_000);

  return () => {
    listeners.delete(onStoreChange);
    if (listeners.size === 0 && ticker) {
      clearInterval(ticker);
      ticker = null;
    }
  };
}

function getSnapshot(): number {
  return currentTime;
}

function elapsedMs(step: ImagingStepDetails, now: number): number | null {
  if (!step.startedAt) return null;
  const started = new Date(step.startedAt).getTime();
  if (Number.isNaN(started)) return null;

  const finished = step.completedAt ? new Date(step.completedAt).getTime() : now;
  return Number.isNaN(finished) ? null : finished - started;
}

function StepKnob({ status }: { status: StepStatus }): React.ReactElement {
  const icon =
    status === 'Completed' ? <Check className="h-3 w-3" strokeWidth={3} aria-hidden="true" />
    : status === 'Warning' ? <AlertTriangle className="h-3 w-3" strokeWidth={3} aria-hidden="true" />
    : status === 'InProgress' ? <LoaderCircle className="h-3 w-3 animate-spin" aria-hidden="true" />
    : status === 'Failed' ? <X className="h-3 w-3" strokeWidth={3} aria-hidden="true" />
    : status === 'NotRun' ? <Minus className="h-3 w-3" strokeWidth={3} aria-hidden="true" />
    : <span className="h-1.5 w-1.5 rounded-full bg-muted-foreground/50" />;

  return (
    <span
      className={cn(
        'absolute left-1/2 top-0 z-10 flex h-[22px] w-[22px] -translate-x-1/2 items-center justify-center rounded-full border-2 bg-card',
        status === 'Completed' && 'border-emerald-600 text-emerald-600 dark:border-emerald-400 dark:text-emerald-400',
        status === 'Warning' && 'border-amber-600 text-amber-600 ring-4 ring-amber-500/15 dark:border-amber-400 dark:text-amber-400',
        status === 'InProgress' && 'border-primary text-primary ring-4 ring-primary/15',
        status === 'Failed' && 'border-destructive text-destructive ring-4 ring-destructive/15',
        status === 'NotRun' && 'border-border text-muted-foreground',
        status === 'Pending' && 'border-border',
      )}
    >
      {icon}
    </span>
  );
}

/** One line per item the device reported for Prepare setup, failures first. */
function StepItems({ sessionId, truncated }: { sessionId: string; truncated: boolean }): React.ReactElement {
  const [items, setItems] = React.useState<ImagingStepItem[] | null>(null);
  const [error, setError] = React.useState<string | null>(null);

  const load = React.useCallback(async () => {
    setError(null);
    setItems(null);
    try {
      setItems(await fetchStepItems(sessionId, PREPARE_SETUP));
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not load the items.');
    }
  }, [sessionId]);

  React.useEffect(() => { void load(); }, [load]);

  if (error) {
    return (
      <div className="mt-2 flex items-center gap-2">
        <p className="text-sm text-destructive">{error}</p>
        <Button variant="link" onClick={() => void load()}>Try again</Button>
      </div>
    );
  }
  if (!items) return <p className="mt-2 text-sm text-muted-foreground">Loading items...</p>;
  if (items.length === 0) return <p className="mt-2 text-sm text-muted-foreground">The device reported no items.</p>;

  return (
    <div className="mt-2 space-y-1">
      <ul className="divide-y divide-border rounded-md border border-border bg-card">
        {items.map((item) => {
          const isScript = item.kind === 'WinPEScript' || item.kind === 'WindowsSetupScript';
          const meta = [
            isScript && item.exitCode != null ? `Exit code ${String(item.exitCode)}` : null,
            isScript && item.durationMs != null ? formatDuration(item.durationMs) : null,
          ].filter(Boolean).join(', ');
          const badge = <Badge variant={OUTCOME_BADGES[item.outcome]} tabIndex={item.reason ? 0 : undefined}>{ITEM_OUTCOME_LABELS[item.outcome]}</Badge>;
          return (
            <li key={item.index} className="flex items-center gap-3 px-3 py-2">
              <span className="min-w-0 flex-1 truncate text-sm">{item.name}</span>
              {meta && <span className="shrink-0 text-xs tabular-nums text-muted-foreground">{meta}</span>}
              {item.reason ? <Tooltip content={item.reason}>{badge}</Tooltip> : badge}
            </li>
          );
        })}
      </ul>
      {truncated && <p className="text-sm text-muted-foreground">Showing the first 500 items.</p>}
    </div>
  );
}

/** Neutral card for Prepare setup: which profile and version, what happened, and the items. */
function PrepareSetupCard({ setupProfile, step, sessionId }: {
  setupProfile: SetupProfilePin | null | undefined;
  step: ResolvedStep | undefined;
  sessionId?: string;
}): React.ReactElement {
  const [open, setOpen] = React.useState(false);
  const listId = React.useId();
  const summary = step?.itemCounts?.length ? itemCountsSummary(step.itemCounts) : null;
  const hasItems = Boolean(sessionId) && (step?.itemCounts?.length ?? 0) > 0;

  return (
    <div className="mt-5 rounded-md border border-border bg-muted/30 p-4">
      <div className="flex flex-wrap items-center justify-between gap-4">
        <div className="min-w-0">
          <p className="text-sm font-medium">Prepare setup</p>
          <p className="text-sm text-muted-foreground">
            {setupProfile ? `${setupProfile.name}, version ${String(setupProfile.version)}` : 'No setup profile'}
          </p>
          {step?.status === 'Warning' && <p className="mt-1 text-sm text-amber-700 dark:text-amber-400">Completed with warnings</p>}
          {summary && <p className="mt-1 text-sm text-foreground">{summary}</p>}
        </div>
        {hasItems && (
          <Button variant="outline" aria-expanded={open} aria-controls={listId} onClick={() => setOpen((o) => !o)}>
            {open ? 'Hide items' : 'Show items'}
          </Button>
        )}
      </div>
      <div id={listId}>
        {open && sessionId && <StepItems sessionId={sessionId} truncated={Boolean(step?.itemsTruncated)} />}
      </div>
    </div>
  );
}

export function SessionProgressDetails({
  overallPercent,
  currentStep,
  steps = [],
  sessionId,
  setupProfile,
  terminal = false,
}: SessionProgressDetailsProps): React.ReactElement {
  const now = React.useSyncExternalStore(subscribe, getSnapshot, getSnapshot);

  const reportedSteps = new Map(steps.map(step => [step.stepName, step]));

  // Optional steps only take a column when reported or expected, so other sessions keep five.
  const shownSteps = PIPELINE_STEPS.filter(definition =>
    !definition.optional || reportedSteps.has(definition.name) || (definition.name === PREPARE_SETUP && Boolean(setupProfile)));

  const resolved: ResolvedStep[] = shownSteps.map(definition => {
    const reported = reportedSteps.get(definition.name);
    const raw = (reported?.status ?? 'Pending') as StepStatus;
    const status: StepStatus =
      raw === 'Completed' && (reported?.warningCount ?? 0) > 0 ? 'Warning'
      // An older boot image never reports the step; once the session ends that is final.
      : !reported && definition.optional && terminal ? 'NotRun'
      : raw;

    return {
      name: definition.name,
      label: definition.label,
      shortLabel: PIPELINE_STEP_SHORT_LABELS[definition.name] ?? definition.label,
      status,
      percent: status === 'Completed' || status === 'Warning' ? 100 : reported?.stepProgressPercent,
      startedAt: reported?.startedAt,
      completedAt: reported?.completedAt,
      errorDetail: reported?.errorDetail,
      durationMs: reported ? elapsedMs(reported, now) : null,
      itemCounts: reported?.itemCounts ?? null,
      itemsTruncated: reported?.itemsTruncated ?? null,
    };
  });

  const failed = resolved.find(step => step.status === 'Failed');
  const running = resolved.find(step => step.status === 'InProgress');
  // The card below the rail carries whichever step needs attention. When neither exists the run is
  // finished or not started, and an empty progress bar there would be noise.
  const focus = failed ?? running;
  const position = resolved.findIndex(step => step.name === (focus?.name ?? currentStep)) + 1;

  const summary = failed
    ? `Failed on ${failed.label}${failed.durationMs != null ? ` after ${formatDuration(failed.durationMs)}` : ''}`
    : running
      ? `Step ${position} of ${resolved.length}, started ${running.startedAt ? formatDateTime(running.startedAt) : 'recently'}`
      : overallPercent >= 100
        ? (resolved.some(step => step.status === 'Warning') ? 'Completed with warnings' : 'All steps completed')
        : 'Waiting for the first progress report';
  const prepareSetup = resolved.find(step => step.name === PREPARE_SETUP);

  return (
    <div className="px-6 py-4">
      <div className="mb-5 flex flex-wrap items-center justify-between gap-4">
        <div>
          <h3 className="text-sm font-semibold text-foreground">Deployment timeline</h3>
          <p className={cn('text-sm', failed ? 'text-destructive' : 'text-muted-foreground')}>{summary}</p>
        </div>
        <div className="flex min-w-48 items-center gap-2">
          <div
            className="h-1.5 flex-1 overflow-hidden rounded-full bg-muted"
            role="progressbar"
            aria-label="Overall deployment progress"
            aria-valuemin={0}
            aria-valuemax={100}
            aria-valuenow={overallPercent}
          >
            <div
              className={cn('h-full rounded-full transition-all', failed ? 'bg-destructive' : 'bg-primary')}
              style={{ width: `${overallPercent}%` }}
            />
          </div>
          <span className="text-xs font-semibold text-foreground">{overallPercent}%</span>
        </div>
      </div>

      {/* Both class names appear literally so Tailwind generates them. */}
      <ol className={cn('grid', resolved.length === 6 ? 'grid-cols-6' : 'grid-cols-5')}>
        {resolved.map((step, index) => (
          <li key={step.name} className="relative pt-7 text-center">
            {/* Two half-width connectors rather than one full-width bar, so the first and last
                knobs do not sprout a rail that leads nowhere. */}
            {index > 0 && (
              <span
                aria-hidden="true"
                className={cn(
                  'absolute left-0 top-[10px] h-0.5 w-1/2',
                  step.status === 'Failed' ? 'bg-destructive/60'
                  : step.status === 'Completed' || step.status === 'Warning' || step.status === 'InProgress'
                    ? 'bg-emerald-600/50 dark:bg-emerald-400/50'
                    : 'bg-border',
                )}
              />
            )}
            {index < resolved.length - 1 && (
              <span
                aria-hidden="true"
                className={cn(
                  'absolute left-1/2 top-[10px] h-0.5 w-1/2',
                  step.status === 'Completed' || step.status === 'Warning' ? 'bg-emerald-600/50 dark:bg-emerald-400/50' : 'bg-border',
                )}
              />
            )}
            <StepKnob status={step.status} />
            <p
              className={cn(
                'px-1 text-[13px]',
                step.status === 'Pending' || step.status === 'NotRun' ? 'text-muted-foreground' : 'font-medium text-foreground',
              )}
            >
              {step.shortLabel}
            </p>
            {step.status === 'NotRun' ? (
              <Tooltip content={NOT_RUN_TOOLTIP}>
                <span tabIndex={0} className="mt-0.5 text-[11px] text-muted-foreground">Not run</span>
              </Tooltip>
            ) : (
              <p className="mt-0.5 text-[11px] tabular-nums text-muted-foreground">
                {step.durationMs != null ? formatDuration(step.durationMs) : '—'}
              </p>
            )}
          </li>
        ))}
      </ol>

      {prepareSetup && <PrepareSetupCard setupProfile={setupProfile} step={prepareSetup} sessionId={sessionId} />}

      {focus && (
        <div
          className={cn(
            'mt-5 flex flex-wrap items-center justify-between gap-4 rounded-md border p-4',
            failed ? 'border-destructive/50 bg-destructive/10' : 'border-border bg-muted/30',
          )}
        >
          <div className="min-w-0">
            <p className={cn('text-sm font-medium', failed && 'text-destructive')}>
              {failed ? `${failed.label} failed` : focus.label}
            </p>
            {focus.errorDetail && (
              <p className="mt-1 select-text text-sm text-foreground">{focus.errorDetail}</p>
            )}
            {focus.startedAt && (
              <p className="mt-1 text-xs text-muted-foreground">
                Started {formatDateTime(focus.startedAt)}
                {focus.completedAt && ` \u00b7 ${failed ? 'Failed' : 'Finished'} ${formatDateTime(focus.completedAt)}`}
                {focus.durationMs != null && ` \u00b7 ${formatDuration(focus.durationMs)}`}
              </p>
            )}
          </div>
          {!failed && focus.percent != null && (
            <div className="flex min-w-60 items-center gap-2">
              <div
                className="h-1.5 flex-1 overflow-hidden rounded-full bg-muted"
                role="progressbar"
                aria-label={`${focus.label} progress`}
                aria-valuemin={0}
                aria-valuemax={100}
                aria-valuenow={focus.percent}
              >
                <div className="h-full rounded-full bg-primary transition-all" style={{ width: `${focus.percent}%` }} />
              </div>
              <span className="text-xs font-semibold tabular-nums text-foreground">{focus.percent}%</span>
            </div>
          )}
        </div>
      )}
    </div>
  );
}