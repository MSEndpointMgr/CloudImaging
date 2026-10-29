import React, { useEffect, useState } from 'react';
import { CircleCheck, Download, ExternalLink, Undo2, X } from 'lucide-react';
import { Button } from '../ui/button.tsx';
import { Skeleton } from '../ui/skeleton.tsx';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '../ui/table.tsx';
import { formatTimestamp } from '../../lib/utils.ts';
import {
  fetchComponentVersions, setRangeLabel, vendorLabel,
  type DriverComponentRow, type DriverComponentVersion, type DriverVendor, type WindowsRelease,
} from '../../lib/drivers.ts';

/** What the details need to show a component's version history and its roll back actions. */
export interface ComponentHistoryContext {
  modelId: string;
  release: WindowsRelease;
  vendor: DriverVendor;
  /** The current driver set, for the open range of the current version. */
  setVersion: number | null;
  /** Present for Operators: opens Roll back component, or undoes a roll back. */
  onRollBack?: (component: DriverComponentRow) => void;
  onUndoRollBack?: (component: DriverComponentRow) => void;
}

function VersionStatus({ version, vendor }: { version: DriverComponentVersion; vendor: DriverVendor }): React.ReactElement {
  if (version.status === 'Stored') {
    return <span className="flex items-center gap-2"><CircleCheck className="h-4 w-4 shrink-0 text-emerald-600 dark:text-emerald-400" aria-hidden="true" />Stored</span>;
  }
  if (version.status === 'DownloadsOnRollBack') {
    return <span className="flex items-center gap-2"><Download className="h-4 w-4 shrink-0 text-blue-600 dark:text-blue-400" aria-hidden="true" />Downloads on roll back</span>;
  }
  return <span className="flex items-center gap-2 text-muted-foreground"><X className="h-4 w-4 shrink-0 text-destructive" aria-hidden="true" />{`No longer available from ${vendorLabel(vendor)}`}</span>;
}

/** The read-only Version history of a component, loaded when its row is expanded. */
function VersionHistory({ component, history }: { component: DriverComponentRow; history: ComponentHistoryContext }): React.ReactElement {
  const [versions, setVersions] = useState<DriverComponentVersion[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    let current = true;
    setError(null);
    fetchComponentVersions(history.modelId, history.release, component.lineageKey)
      .then(list => { if (current) setVersions(list); })
      .catch((err: unknown) => { if (current) setError(err instanceof Error ? err.message : 'Could not load the version history.'); });
    return () => { current = false; };
  }, [history.modelId, history.release, component.lineageKey, component.version, attempt]);

  const rolledBack = !!component.rollBack;
  const nothingEarlier = versions !== null && versions.length < 2;
  return (
    <div className="space-y-2">
      <div className="flex items-center gap-4">
        <h4 className="text-sm font-semibold">Version history</h4>
        {rolledBack && history.onUndoRollBack && (
          <Button variant="outline" className="ml-auto" onClick={() => history.onUndoRollBack?.(component)}><Undo2 />Undo roll back</Button>
        )}
        {!rolledBack && history.onRollBack && (
          <>
            {nothingEarlier && <span className="ml-auto text-sm text-muted-foreground">No earlier version to roll back to.</span>}
            <Button variant="outline" className={nothingEarlier ? undefined : 'ml-auto'} onClick={() => history.onRollBack?.(component)} disabled={versions === null || nothingEarlier}><Undo2 />Roll back component</Button>
          </>
        )}
      </div>
      {error ? (
        <p role="alert" className="flex items-center gap-2 text-sm text-destructive">
          {error}
          <Button variant="link" className="h-auto px-0" onClick={() => setAttempt(n => n + 1)}>Try again</Button>
        </p>
      ) : !versions ? (
        <Skeleton className="h-16 w-full" />
      ) : (
        <div className="overflow-hidden rounded-md border border-border bg-background">
        <Table className="table-fixed">
          <TableHeader>
            <TableRow>
              <TableHead>Version</TableHead>
              <TableHead className="w-[120px]">Driver sets</TableHead>
              <TableHead className="w-[150px]">From</TableHead>
              <TableHead className="w-[150px]">To</TableHead>
              <TableHead className="w-[220px]">Changed by</TableHead>
              <TableHead className="w-[240px]">Status</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {versions.map(v => (
              <TableRow key={`${v.version}-${v.from}`}>
                <TableCell className="truncate" title={v.version}>{v.version}</TableCell>
                <TableCell>
                  <span className={v.lastSet === null ? 'inline-flex rounded-full bg-primary/15 px-2 py-0.5 text-xs font-medium' : 'inline-flex rounded-full bg-muted px-2 py-0.5 text-xs font-medium text-muted-foreground'}>
                    {setRangeLabel(v, history.setVersion)}
                  </span>
                </TableCell>
                <TableCell>{formatTimestamp(v.from)}</TableCell>
                <TableCell>{v.to ? formatTimestamp(v.to) : 'Now'}</TableCell>
                <TableCell className="truncate" title={v.changedBy ?? undefined}>{v.changedBy}</TableCell>
                <TableCell><VersionStatus version={v} vendor={history.vendor} /></TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
        </div>
      )}
    </div>
  );
}

/**
 * Expanded component row: the vendor's labelled fields in a three-column grid, then the
 * description with its release notes link, then the version history. Never an install command,
 * hash or release list.
 */
export function ComponentDetails({ component, history, children }: { component: DriverComponentRow; history?: ComponentHistoryContext; children?: React.ReactNode }): React.ReactElement {
  return (
    <div className="space-y-4 px-4 py-4">
      <dl className="grid grid-cols-3 gap-x-8 gap-y-3">
        {component.details.map(field => (
          <div key={field.label} className="min-w-0">
            <dt className="text-xs text-muted-foreground">{field.label}</dt>
            <dd className="truncate text-sm" title={field.value}>{field.value}</dd>
          </div>
        ))}
      </dl>
      {component.descriptionUrl && (
        <div className="rounded-md border border-border px-4 py-3">
          <div className="text-xs text-muted-foreground">Description</div>
          <a href={component.descriptionUrl} target="_blank" rel="noopener noreferrer" className="inline-flex items-center gap-1 text-sm underline-offset-4 hover:underline">
            Release notes
            <ExternalLink className="h-3 w-3" aria-hidden="true" />
          </a>
        </div>
      )}
      {children}
      {history && <VersionHistory component={component} history={history} />}
    </div>
  );
}
