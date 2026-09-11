import { CheckCircle2, Circle, LoaderCircle, XCircle } from 'lucide-react';
import { Badge, type BadgeProps } from './ui/badge.tsx';
import { formatDateTime } from '../lib/utils.ts';
import { PIPELINE_STEPS, progressStepLabel, type ImagingStepDetails } from '../lib/imagingProgress.ts';

interface SessionProgressDetailsProps {
  overallPercent: number;
  currentStep: string | null;
  steps?: ImagingStepDetails[];
}

function statusVariant(status: string): BadgeProps['variant'] {
  switch (status) {
    case 'Completed':  return 'success';
    case 'InProgress': return 'info';
    case 'Failed':     return 'destructive';
    default:           return 'muted';
  }
}

function statusIcon(status: string): React.ReactElement {
  switch (status) {
    case 'Completed':
      return <CheckCircle2 className="h-4 w-4 text-emerald-600 dark:text-emerald-400" aria-hidden="true" />;
    case 'InProgress':
      return <LoaderCircle className="h-4 w-4 animate-spin text-blue-600 dark:text-blue-400" aria-hidden="true" />;
    case 'Failed':
      return <XCircle className="h-4 w-4 text-destructive" aria-hidden="true" />;
    default:
      return <Circle className="h-4 w-4 text-muted-foreground" aria-hidden="true" />;
  }
}

export function SessionProgressDetails({
  overallPercent,
  currentStep,
  steps = [],
}: SessionProgressDetailsProps): React.ReactElement {
  const reportedSteps = new Map(steps.map(step => [step.stepName, step]));

  return (
    <div className="px-6 py-4">
      <div className="mb-4 flex flex-wrap items-center justify-between gap-4">
        <div>
          <h3 className="text-sm font-semibold text-foreground">Deployment timeline</h3>
          <p className="text-sm text-muted-foreground">Current stage: {progressStepLabel(currentStep)}</p>
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
            <div className="h-full rounded-full bg-primary transition-all" style={{ width: `${overallPercent}%` }} />
          </div>
          <span className="text-xs font-semibold text-foreground">{overallPercent}%</span>
        </div>
      </div>

      <ol className="divide-y divide-border border-y border-border">
        {PIPELINE_STEPS.map(definition => {
          const reported = reportedSteps.get(definition.name);
          const status = reported?.status ?? 'Pending';
          const percent = status === 'Completed' ? 100 : reported?.stepProgressPercent;

          return (
            <li key={definition.name} className="grid grid-cols-[16px_minmax(10rem,1fr)_auto] items-start gap-3 py-3">
              <span className="mt-0.5">{statusIcon(status)}</span>
              <div className="min-w-0">
                <div className="flex flex-wrap items-center gap-2">
                  <span className="text-sm font-medium text-foreground">{definition.label}</span>
                  {percent != null && (
                    <span className="text-xs text-muted-foreground">{percent}%</span>
                  )}
                </div>
                {(reported?.startedAt || reported?.completedAt) && (
                  <p className="mt-1 text-xs text-muted-foreground">
                    {reported.startedAt && `Started ${formatDateTime(reported.startedAt)}`}
                    {reported.startedAt && reported.completedAt && ' \u00b7 '}
                    {reported.completedAt && `Finished ${formatDateTime(reported.completedAt)}`}
                  </p>
                )}
                {reported?.errorDetail && (
                  <p className="mt-1 select-text text-sm text-destructive">{reported.errorDetail}</p>
                )}
              </div>
              <Badge variant={statusVariant(status)} dot>{status}</Badge>
            </li>
          );
        })}
      </ol>
    </div>
  );
}