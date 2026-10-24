import * as React from 'react';
import { Check, CircleCheck, CircleMinus, CircleX, Clock, LoaderCircle, Minus, X } from 'lucide-react';
import { Button } from './ui/button.tsx';
import { Tooltip } from './ui/tooltip.tsx';
import { cn, formatDateTime, formatDuration } from '../lib/utils.ts';
import {
  PIPELINE_STEPS,
  PIPELINE_STEP_SHORT_LABELS,
  type ImagingStepDetails,
} from '../lib/imagingProgress.ts';
import {
  STEP_ITEM_KIND_LABELS,
  fetchStepItems,
  itemCountsSummary,
  itemStatusText,
  itemTooltip,
  sortItemsFailuresFirst,
  type ImagingStepItem,
  type SetupProfilePin,
  type SummaryTone,
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
    : status === 'Warning' ? <span className="text-[13px] font-semibold leading-none" aria-hidden="true">!</span>
    : status === 'InProgress' ? <LoaderCircle className="h-3 w-3 animate-spin" aria-hidden="true" />
    : status === 'Failed' ? <X className="h-3 w-3" strokeWidth={3} aria-hidden="true" />
    : status === 'NotRun' ? <Minus className="h-3 w-3" strokeWidth={3} aria-hidden="true" />
    : <span className="h-1.5 w-1.5 rounded-full bg-muted-foreground/50" />;

  return (
    <span
      className={cn(
        'absolute left-1/2 top-0 z-10 flex h-[22px] w-[22px] -translate-x-1/2 items-center justify-center rounded-full border-2 bg-card',
        status === 'Completed' && 'border-emerald-600 text-emerald-600 dark:border-emerald-400 dark:text-emerald-400',
        status === 'Warning' && 'border-amber-400 bg-amber-400 text-slate-950',
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

const SUMMARY_ICONS: Record<SummaryTone, { icon: typeof CircleCheck; className: string }> = {
  failed: { icon: CircleX, className: 'text-destructive' },
  notRun: { icon: CircleMinus, className: 'text-muted-foreground' },
  succeeded: { icon: CircleCheck, className: 'text-emerald-600 dark:text-emerald-400' },
  queued: { icon: Clock, className: 'text-muted-foreground' },
};

/** One-line rows, failures first; the full reason is in the row tooltip. */
function ItemRows({ items }: { items: ImagingStepItem[] }): React.ReactElement {
  return (
    <ul className="mt-3 divide-y divide-border rounded-md border border-border bg-background">
      {items.map((item) => {
        const tone: SummaryTone = item.outcome === 'Failed' ? 'failed' : item.outcome === 'NotRun' ? 'notRun' : item.outcome === 'Queued' ? 'queued' : 'succeeded';
        const { icon: Icon, className } = SUMMARY_ICONS[tone];
        const tooltip = itemTooltip(item);
        const row = (
          <span tabIndex={tooltip ? 0 : undefined} className="grid h-10 w-full grid-cols-[16px_150px_minmax(0,1fr)_minmax(0,300px)] items-center gap-3 px-3 text-sm">
            <Icon className={cn('h-4 w-4', className)} aria-hidden="true" />
            <span className="truncate">{STEP_ITEM_KIND_LABELS[item.kind]}</span>
            <span className="truncate">{item.name}</span>
            <span className="truncate text-muted-foreground">{itemStatusText(item)}</span>
          </span>
        );
        return <li key={item.index}>{tooltip ? <Tooltip content={tooltip} className="block w-full">{row}</Tooltip> : row}</li>;
      })}
    </ul>
  );
}

/**
 * Prepare setup card: which profile and version ran, what happened, and the items. Failed items
 * load straight away so a warning never hides behind a click; everything else is behind Show details.
 */
function PrepareSetupCard({ setupProfile, step, sessionId }: {
  setupProfile: SetupProfilePin | null | undefined;
  step: ResolvedStep | undefined;
  sessionId?: string;
}): React.ReactElement {
  const [showAll, setShowAll] = React.useState(false);
  const [items, setItems] = React.useState<ImagingStepItem[] | null>(null);
  const [error, setError] = React.useState<string | null>(null);
  const listId = React.useId();
  const counts = step?.itemCounts ?? [];
  const summary = itemCountsSummary(counts);
  const totalCount = counts.reduce((sum, c) => sum + c.count, 0);
  const failedCount = counts.filter((c) => c.outcome === 'Failed').reduce((sum, c) => sum + c.count, 0);
  const queuedCount = counts.filter((c) => c.outcome === 'Queued').reduce((sum, c) => sum + c.count, 0);
  const wantItems = Boolean(sessionId) && totalCount > 0 && (showAll || failedCount > 0);

  const load = React.useCallback(async () => {
    if (!sessionId) return;
    setError(null);
    try {
      setItems(await fetchStepItems(sessionId, PREPARE_SETUP));
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not load the items.');
    }
  }, [sessionId]);

  React.useEffect(() => {
    if (wantItems && items === null) void load();
  }, [wantItems, items, load]);

  const sorted = items ? sortItemsFailuresFirst(items) : [];
  const shown = showAll ? sorted : sorted.filter((i) => i.outcome === 'Failed');
  const hidden = Math.max(0, totalCount - shown.length);

  return (
    <div className="mt-5 rounded-md border border-border bg-muted/30 p-4">
      <p className="text-sm font-medium">
        {setupProfile ? `Setup profile: ${setupProfile.name}, version ${String(setupProfile.version)}` : 'No setup profile'}
      </p>
      {(summary.length > 0 || (Boolean(sessionId) && totalCount > 0)) && (
        <div className="mt-2 flex flex-wrap items-center gap-4 text-sm text-muted-foreground">
          {summary.map(({ tone, text }) => {
            const { icon: Icon, className } = SUMMARY_ICONS[tone];
            return <span key={text} className="inline-flex items-center gap-2"><Icon className={cn('h-4 w-4', className)} aria-hidden="true" />{text}</span>;
          })}
          {Boolean(sessionId) && totalCount > 0 && (
            <Button variant="link" aria-expanded={showAll} aria-controls={listId} onClick={() => setShowAll((s) => !s)}>
              {showAll ? 'Hide details' : 'Show details'}
            </Button>
          )}
        </div>
      )}
      <div id={listId}>
        {wantItems && error && (
          <div className="mt-2 flex items-center gap-2">
            <p className="text-sm text-destructive">{error}</p>
            <Button variant="link" onClick={() => void load()}>Try again</Button>
          </div>
        )}
        {wantItems && !error && items === null && <p className="mt-2 text-sm text-muted-foreground">Loading items...</p>}
        {wantItems && items !== null && shown.length > 0 && <ItemRows items={shown} />}
        {wantItems && items !== null && items.length === 0 && <p className="mt-2 text-sm text-muted-foreground">The device reported no items.</p>}
      </div>
      {!showAll && failedCount > 0 && items !== null && hidden > 0 && (
        <p className="mt-2 text-xs text-muted-foreground">Failures are listed first; {plural(hidden, 'other item is', 'other items are')} hidden.</p>
      )}
      {showAll && step?.itemsTruncated && <p className="mt-2 text-xs text-muted-foreground">Showing the first 500 items.</p>}
      {queuedCount > 0 && step?.status !== 'Failed' && (
        <p className="mt-2 text-xs text-muted-foreground">Windows setup runs after the restart and is recorded on the device only.</p>
      )}
      {step?.status === 'Failed' && (
        <p className="mt-2 text-xs text-muted-foreground">The device does not start Windows. Retry the deployment after fixing the profile or the OS image.</p>
      )}
    </div>
  );
}

function plural(count: number, one: string, many: string): string {
  return `${String(count)} ${count === 1 ? one : many}`;
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

      <ol className="grid" style={{ gridTemplateColumns: `repeat(${String(resolved.length)}, minmax(0, 1fr))` }}>
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
                  : step.status === 'Warning' ? 'bg-amber-400/50'
                  : step.status === 'Completed' || step.status === 'InProgress'
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
                  step.status === 'Warning' ? 'bg-amber-400/50'
                  : step.status === 'Completed' ? 'bg-emerald-600/50 dark:bg-emerald-400/50' : 'bg-border',
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
                {step.durationMs != null ? formatDuration(step.durationMs) : '\u2014'}
              </p>
            )}
          </li>
        ))}
      </ol>

      {prepareSetup && <PrepareSetupCard setupProfile={setupProfile} step={prepareSetup} sessionId={sessionId} />}

      {/* A failed Prepare setup already explains itself in its own card. */}
      {focus && !(failed && failed.name === PREPARE_SETUP) && (
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