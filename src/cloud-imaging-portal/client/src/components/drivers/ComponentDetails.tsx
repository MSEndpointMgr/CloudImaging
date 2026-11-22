import React, { useEffect, useId, useState } from 'react';
import { ChevronDown, ChevronRight, CircleCheck, CircleX, Download, ExternalLink, Undo2, X } from 'lucide-react';
import { Button } from '../ui/button.tsx';
import { Skeleton } from '../ui/skeleton.tsx';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '../ui/table.tsx';
import { formatTimestamp } from '../../lib/utils.ts';
import {
  fetchComponentVersions, fetchFirmwareVersions, setRangeLabel, vendorLabel, VERSION_CHANGE_LABELS,
  type DriverComponentRow, type DriverComponentVersion, type DriverDetailField, type DriverVendor, type WindowsRelease,
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
  if (version.status === 'Downloading') {
    return <span className="flex items-center gap-2"><Download className="h-4 w-4 shrink-0 text-blue-600 dark:text-blue-400" aria-hidden="true" />Downloading</span>;
  }
  if (version.status === 'Failed') {
    return <span className="flex items-center gap-2"><CircleX className="h-4 w-4 shrink-0 text-destructive" aria-hidden="true" />Download failed</span>;
  }
  if (version.status === 'NotDownloaded') {
    return <span className="text-muted-foreground">Not downloaded</span>;
  }
  return <span className="flex items-center gap-2 text-muted-foreground"><X className="h-4 w-4 shrink-0 text-destructive" aria-hidden="true" />{`No longer available from ${vendorLabel(vendor)}`}</span>;
}

/** Where a version history is read from: a driver component of one release, or a model's BIOS. */
export type VersionHistorySource =
  | { kind: 'component'; modelId: string; release: WindowsRelease; lineageKey: string; version: string | null }
  | { kind: 'firmware'; modelId: string; platformId: string; version: string };

/**
 * The Version history box, loaded when its row is expanded and shown collapsed, since it grows with
 * every update. `setVersion` adds the Driver sets column; the BIOS has none, being the same for
 * every Windows release.
 */
export function VersionHistoryBox({ source, vendor, setVersion, rolledBack, onRollBack, onUndoRollBack, refreshKey }: {
  source: VersionHistorySource;
  vendor: DriverVendor;
  setVersion?: number | null;
  rolledBack: boolean;
  onRollBack?: () => void;
  onUndoRollBack?: () => void;
  /** The row the history belongs to: a new one, read after a download or a build, reads the history again. */
  refreshKey?: unknown;
}): React.ReactElement {
  const [versions, setVersions] = useState<DriverComponentVersion[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [attempt, setAttempt] = useState(0);
  const [open, setOpen] = useState(false);
  const bodyId = useId();
  const showSets = setVersion !== undefined;

  const { kind, modelId, version } = source;
  const key = source.kind === 'component' ? `${source.release}|${source.lineageKey}` : source.platformId;
  useEffect(() => {
    let current = true;
    setError(null);
    const [first, second] = key.split('|');
    const read = kind === 'component'
      ? fetchComponentVersions(modelId, first as WindowsRelease, second)
      : fetchFirmwareVersions(modelId, first);
    read
      .then(list => { if (current) setVersions(list); })
      .catch((err: unknown) => { if (current) setError(err instanceof Error ? err.message : 'Could not load the version history.'); });
    return () => { current = false; };
  }, [kind, modelId, key, version, attempt, refreshKey]);

  const nothingEarlier = versions !== null && versions.length < 2;
  return (
    <div className="overflow-hidden rounded-md border border-border bg-background">
      {/* px-3, not px-4: the chevron's stroke sits 3 to 5px inside its box, which puts it on the line of the text. */}
      <div className="flex items-center gap-4 px-3 py-2">
        <button
          type="button"
          aria-expanded={open}
          aria-controls={bodyId}
          onClick={() => setOpen(o => !o)}
          className="flex min-w-0 flex-1 items-center gap-2 rounded-md text-left text-sm font-semibold focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
        >
          {open ? <ChevronDown className="h-4 w-4 shrink-0 text-muted-foreground" aria-hidden="true" /> : <ChevronRight className="h-4 w-4 shrink-0 text-muted-foreground" aria-hidden="true" />}
          Version history
          {versions && <span className="font-normal text-muted-foreground">{versions.length === 1 ? '1 version' : `${versions.length} versions`}</span>}
        </button>
        {rolledBack && onUndoRollBack && (
          <Button variant="outline" onClick={onUndoRollBack}><Undo2 />Undo roll back</Button>
        )}
        {!rolledBack && onRollBack && (
          <>
            {nothingEarlier && <span className="text-sm text-muted-foreground">No earlier version to roll back to.</span>}
            <Button variant="outline" onClick={onRollBack} disabled={versions === null || nothingEarlier}><Undo2 />Roll back component</Button>
          </>
        )}
      </div>
      {open && (
        <div id={bodyId} className="border-t border-border">
          {error ? (
            <p role="alert" className="flex items-center gap-2 px-4 py-2 text-sm text-destructive">
              {error}
              <Button variant="link" className="h-auto px-0" onClick={() => setAttempt(n => n + 1)}>Try again</Button>
            </p>
          ) : !versions ? (
            <Skeleton className="m-3 h-16" />
          ) : (
            <Table className="table-fixed">
              <TableHeader>
                <TableRow>
                  <TableHead className="w-[180px] pl-4">Version</TableHead>
                  {showSets && <TableHead className="w-[110px]">Driver sets</TableHead>}
                  <TableHead className="w-[150px]">From</TableHead>
                  <TableHead className="w-[150px]">To</TableHead>
                  <TableHead className="w-[150px]">Change</TableHead>
                  <TableHead>By</TableHead>
                  <TableHead className="w-[220px]">Status</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {versions.map(v => (
                  <TableRow key={`${v.version}-${v.from}`}>
                    <TableCell className="truncate pl-4" title={v.version}>{v.version}</TableCell>
                    {showSets && (
                      <TableCell>
                        <span className={v.lastSet === null ? 'inline-flex rounded-full bg-primary/15 px-2 py-0.5 text-xs font-medium' : 'inline-flex rounded-full bg-muted px-2 py-0.5 text-xs font-medium text-muted-foreground'}>
                          {setRangeLabel(v, setVersion ?? null)}
                        </span>
                      </TableCell>
                    )}
                    <TableCell>{formatTimestamp(v.from)}</TableCell>
                    <TableCell>{v.to ? formatTimestamp(v.to) : 'Now'}</TableCell>
                    <TableCell>{VERSION_CHANGE_LABELS[v.change] ?? v.change}</TableCell>
                    <TableCell className="truncate" title={v.changedBy ?? undefined}>{v.changedBy ?? <span className="text-muted-foreground">Automatic</span>}</TableCell>
                    <TableCell><VersionStatus version={v} vendor={vendor} /></TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          )}
        </div>
      )}
    </div>
  );
}

/** The Version history of a driver component. */
function VersionHistory({ component, history }: { component: DriverComponentRow; history: ComponentHistoryContext }): React.ReactElement {
  return (
    <VersionHistoryBox
      source={{ kind: 'component', modelId: history.modelId, release: history.release, lineageKey: component.lineageKey, version: component.version }}
      vendor={history.vendor}
      setVersion={history.setVersion}
      rolledBack={!!component.rollBack}
      onRollBack={history.onRollBack ? () => history.onRollBack?.(component) : undefined}
      onUndoRollBack={history.onUndoRollBack ? () => history.onUndoRollBack?.(component) : undefined}
      refreshKey={component}
    />
  );
}

/**
 * Labelled fields in a three-column grid. The transparent border and px-4 match the boxes below
 * them, so the labels start on the same line as the text inside those boxes.
 */
export function DetailFields({ fields }: { fields: DriverDetailField[] }): React.ReactElement {
  return (
    <dl className="grid grid-cols-3 gap-x-8 gap-y-3 border-x border-transparent px-4">
      {fields.map(field => (
        <div key={field.label} className="min-w-0">
          <dt className="text-xs text-muted-foreground">{field.label}</dt>
          <dd className="truncate text-sm" title={field.value}>{field.value}</dd>
        </div>
      ))}
    </dl>
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
      <DetailFields fields={component.details} />
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
