import React, { useCallback, useEffect, useState } from 'react';
import { CircleCheck, CircleX, Clock, Download, RefreshCw } from 'lucide-react';
import { Button } from '../ui/button.tsx';
import { EmptyState } from '../ui/empty-state.tsx';
import { Skeleton } from '../ui/skeleton.tsx';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '../ui/table.tsx';
import { Tooltip } from '../ui/tooltip.tsx';
import { formatTimestamp } from '../../lib/utils.ts';
import { fetchDownloads, formatSize, releaseLabel, sizeOfLabel, type DriverDownloadInProgress, type DriverDownloads } from '../../lib/drivers.ts';

/** How often the tab reads again while anything is in progress. */
export const DOWNLOADS_POLL_MS = 15_000;

function SetPill({ version }: { version: number }): React.ReactElement {
  return <span className="inline-flex rounded-full bg-muted px-2 py-0.5 text-xs font-medium text-muted-foreground">{`Set ${version}`}</span>;
}

function StatusLine({ icon, label }: { icon: React.ReactNode; label: string }): React.ReactElement {
  return (
    <span className="flex min-w-0 items-center gap-2">
      {icon}
      <span className="truncate">{label}</span>
    </span>
  );
}

function ProgressStatus({ row }: { row: DriverDownloadInProgress }): React.ReactElement {
  if (row.status === 'Failed') {
    const reason = row.reasons.join(' ');
    return (
      <StatusLine
        icon={(
          <Tooltip content={reason}>
            <span tabIndex={0} aria-label={reason} className="inline-flex">
              <CircleX className="h-4 w-4 text-destructive" aria-hidden="true" />
            </span>
          </Tooltip>
        )}
        label={`${row.failed} failed`}
      />
    );
  }
  if (row.status === 'Waiting') {
    return <StatusLine icon={<Clock className="h-4 w-4 text-muted-foreground" aria-hidden="true" />} label="Waiting" />;
  }
  return <StatusLine icon={<Download className="h-4 w-4 text-blue-600 dark:text-blue-400" aria-hidden="true" />} label="Downloading" />;
}

function Progress({ row }: { row: DriverDownloadInProgress }): React.ReactElement {
  const label = `${row.componentsDone} of ${row.components} components`;
  const percent = row.components === 0 ? 0 : Math.round((row.componentsDone / row.components) * 100);
  return (
    <div className="flex items-center gap-2">
      <div role="progressbar" aria-label={`${row.modelName}, set ${row.setVersion}`} aria-valuemin={0} aria-valuemax={row.components} aria-valuenow={row.componentsDone} aria-valuetext={label} className="h-2 w-24 shrink-0 overflow-hidden rounded-full bg-muted">
        <div className="h-full bg-primary" style={{ width: `${percent}%` }} />
      </div>
      <span className="truncate">{label}</span>
    </div>
  );
}

/**
 * Downloads tab: driver sets being prepared, then those that became ready in the last 7 days.
 * Polls while anything is in progress and reports the in-progress count for the tab label.
 */
export function DownloadsTab({ onCount }: { onCount?: (inProgress: number) => void }): React.ReactElement {
  const [downloads, setDownloads] = useState<DriverDownloads | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      const loaded = await fetchDownloads();
      setDownloads(loaded);
      setLoadError(null);
      onCount?.(loaded.inProgress.length);
    } catch (err) {
      setLoadError(err instanceof Error ? err.message : 'Could not load downloads.');
    }
  }, [onCount]);

  useEffect(() => { void load(); }, [load]);

  const busy = (downloads?.inProgress.length ?? 0) > 0;
  useEffect(() => {
    if (!busy) return undefined;
    const timer = window.setInterval(() => { void load(); }, DOWNLOADS_POLL_MS);
    return () => window.clearInterval(timer);
  }, [busy, load]);

  if (loadError && !downloads) {
    return <EmptyState icon={RefreshCw} title="Could not load downloads" description={loadError} action={<Button variant="outline" onClick={() => void load()}><RefreshCw />Try again</Button>} />;
  }
  if (!downloads) {
    return <div className="space-y-4"><Skeleton className="h-6 w-48" /><Skeleton className="h-32 w-full" /></div>;
  }

  return (
    <div className="space-y-8">
      {loadError && (
        <div role="alert" className="flex items-center gap-2 text-sm">
          <CircleX className="h-4 w-4 shrink-0 text-destructive" aria-hidden="true" />
          <span className="text-destructive">{`The downloads could not be refreshed: ${loadError} The progress below may be out of date.`}</span>
          <Button variant="link" onClick={() => void load()}>Try again</Button>
        </div>
      )}
      <section className="space-y-4" aria-labelledby="downloads-in-progress">
        <div>
          <h3 id="downloads-in-progress" className="text-base font-semibold">In progress</h3>
          <p className="text-sm text-muted-foreground">Files queued by completing onboarding, an action change, a sync or a check for updates. A new driver set reaches devices only when all its components are downloaded.</p>
        </div>
        <Table className="table-fixed">
          <TableHeader>
            <TableRow>
              <TableHead>Model</TableHead>
              <TableHead className="w-[200px]">Windows</TableHead>
              <TableHead className="w-[100px]">Driver set</TableHead>
              <TableHead className="w-[240px]">Progress</TableHead>
              <TableHead className="w-[130px] text-right">Size</TableHead>
              <TableHead className="w-[140px]">Status</TableHead>
              <TableHead className="w-[150px]">Started</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {downloads.inProgress.map(row => (
              <TableRow key={`${row.modelId}-${row.release}-${row.setVersion}`}>
                <TableCell className="truncate font-medium" title={row.modelName}>{row.modelName}</TableCell>
                <TableCell className="truncate">{releaseLabel(row.release)}</TableCell>
                <TableCell><SetPill version={row.setVersion} /></TableCell>
                <TableCell><Progress row={row} /></TableCell>
                <TableCell className="text-right">{sizeOfLabel(row.bytesDone, row.bytes)}</TableCell>
                <TableCell><ProgressStatus row={row} /></TableCell>
                <TableCell>{formatTimestamp(row.startedAt)}</TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
        {downloads.inProgress.length === 0 && (
          <EmptyState icon={CircleCheck} title="No downloads in progress" description="Every driver set is ready. Downloads start after completing onboarding, an action change or a sync." />
        )}
      </section>

      <section className="space-y-4" aria-labelledby="downloads-finished">
        <h3 id="downloads-finished" className="text-base font-semibold">Finished in the last 7 days</h3>
        {downloads.finished.length === 0 ? (
          <p className="text-sm text-muted-foreground">No driver set became ready in the last 7 days.</p>
        ) : (
          <Table className="table-fixed">
            <TableHeader>
              <TableRow>
                <TableHead>Model</TableHead>
                <TableHead className="w-[200px]">Windows</TableHead>
                <TableHead className="w-[100px]">Driver set</TableHead>
                <TableHead className="w-[240px]">Components</TableHead>
                <TableHead className="w-[130px] text-right">Size</TableHead>
                <TableHead className="w-[140px]">Status</TableHead>
                <TableHead className="w-[150px]">Finished</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {downloads.finished.map(row => (
                <TableRow key={`${row.modelId}-${row.release}-${row.setVersion}`}>
                  <TableCell className="truncate font-medium" title={row.modelName}>{row.modelName}</TableCell>
                  <TableCell className="truncate">{releaseLabel(row.release)}</TableCell>
                  <TableCell><SetPill version={row.setVersion} /></TableCell>
                  <TableCell className="truncate">{`${row.newComponents} new, ${row.reusedComponents} already stored`}</TableCell>
                  <TableCell className="text-right">{formatSize(row.sizeBytes)}</TableCell>
                  <TableCell><StatusLine icon={<CircleCheck className="h-4 w-4 text-emerald-600 dark:text-emerald-400" aria-hidden="true" />} label="Ready" /></TableCell>
                  <TableCell>{formatTimestamp(row.readyAt)}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </section>
    </div>
  );
}
