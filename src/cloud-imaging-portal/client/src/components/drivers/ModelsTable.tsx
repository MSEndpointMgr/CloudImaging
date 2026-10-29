import React from 'react';
import { ChevronRight, CircleAlert, CircleCheck, CircleDashed, CircleX, Cpu, Download, FlaskConical, Info, RefreshCw, Search } from 'lucide-react';
import { Badge } from '../ui/badge.tsx';
import { Button } from '../ui/button.tsx';
import { EmptyState } from '../ui/empty-state.tsx';
import { TableSkeletonRows } from '../ui/skeleton.tsx';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '../ui/table.tsx';
import { Tooltip } from '../ui/tooltip.tsx';
import { formatSize, releaseLabel, releaseVersion, vendorLabel, type DriverModelRow } from '../../lib/drivers.ts';

export const SIZE_HINT = 'The most one device downloads. Devices without some hardware can download less.';

/** Header label with the Size info icon. */
export function SizeHead(): React.ReactElement {
  return (
    <span className="inline-flex items-center gap-1">
      Size
      <Tooltip content={SIZE_HINT}>
        <span tabIndex={0} aria-label={SIZE_HINT} className="inline-flex text-muted-foreground">
          <Info className="h-3 w-3" aria-hidden="true" />
        </span>
      </Tooltip>
    </span>
  );
}

/** Icon and label for what needs attention or is in progress, or Ready when devices get the current set. */
export function ModelStatus({ model }: { model: DriverModelRow }): React.ReactElement {
  const { status } = model;
  if (model.state === 'Draft') {
    return <StatusLine icon={<CircleDashed className="h-4 w-4 text-muted-foreground" aria-hidden="true" />} label="Draft" />;
  }
  if (status.downloading) {
    return <StatusLine icon={<Download className="h-4 w-4 text-blue-600 dark:text-blue-400" aria-hidden="true" />} label={`Downloading ${status.downloading.done} of ${status.downloading.total}`} />;
  }
  if (status.failedItems > 0) {
    const reason = status.failureReason ?? 'A component could not be downloaded or verified. Open the model to see which.';
    return (
      <StatusLine
        icon={(
          <Tooltip content={reason}>
            <span tabIndex={0} aria-label={reason} className="inline-flex">
              <CircleX className="h-4 w-4 text-destructive" aria-hidden="true" />
            </span>
          </Tooltip>
        )}
        label={`${status.failedItems} failed`}
      />
    );
  }
  if (status.waitingForApproval > 0) {
    return <StatusLine icon={<CircleAlert className="h-4 w-4 text-amber-600 dark:text-amber-400" aria-hidden="true" />} label={`${status.waitingForApproval} waiting for approval`} />;
  }
  if (status.inTesting) {
    return <StatusLine icon={<FlaskConical className="h-4 w-4 text-blue-600 dark:text-blue-400" aria-hidden="true" />} label="In testing" />;
  }
  if (status.syncOff) {
    return <span className="text-sm text-muted-foreground">Sync off</span>;
  }
  return <StatusLine icon={<CircleCheck className="h-4 w-4 text-emerald-600 dark:text-emerald-400" aria-hidden="true" />} label="Ready" />;
}

function StatusLine({ icon, label }: { icon: React.ReactNode; label: string }): React.ReactElement {
  return (
    <span className="flex min-w-0 items-center gap-2">
      {icon}
      <span className="truncate">{label}</span>
    </span>
  );
}

/** The Models tab table: one line per onboarded model; a row opens the model's page. */
export function ModelsTable({ models, total, loadError, onRetry, emptyAction, onOpen }: {
  /** The models after search and filters; null while loading. */
  models: DriverModelRow[] | null;
  /** How many models are onboarded, to tell "none yet" from "none match". */
  total: number;
  loadError: string | null;
  onRetry: () => void;
  emptyAction?: React.ReactNode;
  onOpen: (modelId: string) => void;
}): React.ReactElement {
  const message = (content: React.ReactNode) => (
    <TableRow className="hover:bg-transparent">
      <TableCell colSpan={8} className="p-0">{content}</TableCell>
    </TableRow>
  );
  return (
    <div className="overflow-hidden rounded-md border border-border">
    <Table className="table-fixed">
      <TableHeader>
        <TableRow>
          <TableHead className="w-[34%]">Model</TableHead>
          <TableHead className="w-[7%]">Vendor</TableHead>
          <TableHead className="w-[13%]">Windows releases</TableHead>
          <TableHead className="w-[9%] text-right">Included</TableHead>
          <TableHead className="w-[9%] text-right">Skipped</TableHead>
          <TableHead className="w-[9%] text-right"><SizeHead /></TableHead>
          <TableHead className="w-[15%]">Status</TableHead>
          <TableHead className="w-[4%]"><span className="sr-only">Open</span></TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {loadError ? message(
          <EmptyState icon={RefreshCw} title="Could not load driver models" description={loadError} action={<Button variant="outline" onClick={onRetry}><RefreshCw />Try again</Button>} />,
        ) : models === null ? (
          <TableSkeletonRows columns={8} rows={3} />
        ) : total === 0 ? message(
          <EmptyState icon={Cpu} title="No models onboarded yet" action={emptyAction} />,
        ) : models.length === 0 ? message(
          <EmptyState icon={Search} title="No models match" description="Clear the search or choose All vendors to see every model." />,
        ) : models.map(model => {
          const releases = model.releases.map(releaseLabel).join(', ');
          return (
            <TableRow key={model.modelId} className="h-14 cursor-pointer" onClick={() => onOpen(model.modelId)}>
              <TableCell>
                <div className="truncate font-medium" title={model.displayName}>{model.displayName}</div>
                <div className="truncate text-xs text-muted-foreground">{model.matchKeys.join(', ')}</div>
              </TableCell>
              <TableCell className="truncate">{vendorLabel(model.vendor)}</TableCell>
              <TableCell>
                <span className="flex gap-1 overflow-hidden" aria-label={releases}>
                  {model.releases.map(r => <Badge key={r} variant="outline" className="shrink-0">{releaseVersion(r)}</Badge>)}
                </span>
              </TableCell>
              <TableCell className="text-right">{model.included}</TableCell>
              <TableCell className="text-right">{model.skipped}</TableCell>
              <TableCell className="text-right">{formatSize(model.maxDownloadBytes)}</TableCell>
              <TableCell><ModelStatus model={model} /></TableCell>
              <TableCell>
                <button
                  type="button"
                  aria-label={`Open ${model.displayName}`}
                  className="inline-flex rounded-md p-1 text-muted-foreground hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
                  onClick={event => { event.stopPropagation(); onOpen(model.modelId); }}
                >
                  <ChevronRight className="h-4 w-4" aria-hidden="true" />
                </button>
              </TableCell>
            </TableRow>
          );
        })}
      </TableBody>
    </Table>
    </div>
  );
}
