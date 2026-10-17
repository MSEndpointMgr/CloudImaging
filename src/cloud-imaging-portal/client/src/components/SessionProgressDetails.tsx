import * as React from 'react';
import { Check, LoaderCircle, X } from 'lucide-react';
import { cn, formatDateTime, formatDuration } from '../lib/utils.ts';
import {
  PIPELINE_STEPS,
  PIPELINE_STEP_SHORT_LABELS,
  type ImagingStepDetails,
} from '../lib/imagingProgress.ts';

interface SessionProgressDetailsProps {
  overallPercent: number;
  currentStep: string | null;
  steps?: ImagingStepDetails[];
}

type StepStatus = 'Completed' | 'InProgress' | 'Failed' | 'Pending';

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
    : status === 'InProgress' ? <LoaderCircle className="h-3 w-3 animate-spin" aria-hidden="true" />
    : status === 'Failed' ? <X className="h-3 w-3" strokeWidth={3} aria-hidden="true" />
    : <span className="h-1.5 w-1.5 rounded-full bg-muted-foreground/50" />;

  return (
    <span
      className={cn(
        'absolute left-1/2 top-0 z-10 flex h-[22px] w-[22px] -translate-x-1/2 items-center justify-center rounded-full border-2 bg-card',
        status === 'Completed' && 'border-emerald-600 text-emerald-600 dark:border-emerald-400 dark:text-emerald-400',
        status === 'InProgress' && 'border-primary text-primary ring-4 ring-primary/15',
        status === 'Failed' && 'border-destructive text-destructive ring-4 ring-destructive/15',
        status === 'Pending' && 'border-border',
      )}
    >
      {icon}
    </span>
  );
}

export function SessionProgressDetails({
  overallPercent,
  currentStep,
  steps = [],
}: SessionProgressDetailsProps): React.ReactElement {
  const now = React.useSyncExternalStore(subscribe, getSnapshot, getSnapshot);

  const reportedSteps = new Map(steps.map(step => [step.stepName, step]));

  const resolved: ResolvedStep[] = PIPELINE_STEPS.map(definition => {
    const reported = reportedSteps.get(definition.name);
    const status = (reported?.status ?? 'Pending') as StepStatus;

    return {
      name: definition.name,
      label: definition.label,
      shortLabel: PIPELINE_STEP_SHORT_LABELS[definition.name] ?? definition.label,
      status,
      percent: status === 'Completed' ? 100 : reported?.stepProgressPercent,
      startedAt: reported?.startedAt,
      completedAt: reported?.completedAt,
      errorDetail: reported?.errorDetail,
      durationMs: reported ? elapsedMs(reported, now) : null,
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
        ? 'All steps completed'
        : 'Waiting for the first progress report';

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

      <ol className="grid grid-cols-5">
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
                  step.status === 'Completed' ? 'bg-emerald-600/50 dark:bg-emerald-400/50' : 'bg-border',
                )}
              />
            )}
            <StepKnob status={step.status} />
            <p
              className={cn(
                'px-1 text-[13px]',
                step.status === 'Pending' ? 'text-muted-foreground' : 'font-medium text-foreground',
              )}
            >
              {step.shortLabel}
            </p>
            <p className="mt-0.5 text-[11px] tabular-nums text-muted-foreground">
              {step.durationMs != null ? formatDuration(step.durationMs) : '—'}
            </p>
          </li>
        ))}
      </ol>

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