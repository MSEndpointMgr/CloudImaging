import React, { Fragment, useCallback, useEffect, useState } from 'react';
import { ChevronDown, ChevronRight, CircleCheck, CircleX, Download, HardDrive, Info, RefreshCw } from 'lucide-react';
import { Button } from '../ui/button.tsx';
import { Card, CardContent } from '../ui/card.tsx';
import { EmptyState } from '../ui/empty-state.tsx';
import { Select } from '../ui/select.tsx';
import { Skeleton } from '../ui/skeleton.tsx';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '../ui/table.tsx';
import { Tooltip } from '../ui/tooltip.tsx';
import { useAuth } from '../../context/authContext.tsx';
import { useToast } from '../../context/toastContext.tsx';
import { formatTimestamp } from '../../lib/utils.ts';
import {
  PREVIOUS_VERSION_OPTIONS, fetchStorage, fetchStorageModel, formatSize, releaseLabel, setCountsLabel, setPillLabel, setPreviousVersionsToKeep, sharedWithLabel, vendorLabel,
  type DriverStorage, type DriverStorageComponent, type DriverStorageModel,
} from '../../lib/drivers.ts';

const TOTAL_HINT = 'Components shared by several models are stored once, so the total is less than the sum of the models.';

function Tile({ label, value }: { label: React.ReactNode; value: string }): React.ReactElement {
  return (
    <div className="rounded-lg border border-border px-4 py-3">
      <div className="text-xs text-muted-foreground">{label}</div>
      <div className="text-lg font-semibold">{value}</div>
    </div>
  );
}

function Pill({ children }: { children: React.ReactNode }): React.ReactElement {
  return <span className="inline-flex rounded-full bg-muted px-2 py-0.5 text-xs font-medium text-muted-foreground">{children}</span>;
}

function StoredStatus({ component }: { component: DriverStorageComponent }): React.ReactElement {
  if (component.status === 'Failed') {
    const reason = component.reason ?? 'The download failed.';
    return (
      <span className="flex min-w-0 items-center gap-2">
        <Tooltip content={reason}>
          <span tabIndex={0} aria-label={reason} className="inline-flex shrink-0">
            <CircleX className="h-4 w-4 text-destructive" aria-hidden="true" />
          </span>
        </Tooltip>
        <span className="truncate">Failed</span>
      </span>
    );
  }
  const downloading = component.status === 'Downloading';
  return (
    <span className="flex min-w-0 items-center gap-2">
      {downloading
        ? <Download className="h-4 w-4 shrink-0 text-blue-600 dark:text-blue-400" aria-hidden="true" />
        : <CircleCheck className="h-4 w-4 shrink-0 text-emerald-600 dark:text-emerald-400" aria-hidden="true" />}
      <span className="truncate">{downloading ? 'Downloading' : 'Stored'}</span>
    </span>
  );
}

/** One model expanded: a block per Windows release with its sets and stored components. */
function ModelBlocks({ modelId }: { modelId: string }): React.ReactElement {
  const [model, setModel] = useState<DriverStorageModel | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    fetchStorageModel(modelId)
      .then(loaded => { if (!cancelled) setModel(loaded); })
      .catch((err: unknown) => { if (!cancelled) setError(err instanceof Error ? err.message : 'Could not load the stored components.'); });
    return () => { cancelled = true; };
  }, [modelId]);

  if (error) return <p className="px-4 py-3 text-sm text-destructive">{error}</p>;
  if (!model) return <div className="px-4 py-3"><Skeleton className="h-6 w-full" /></div>;
  return (
    <div className="space-y-6 px-4 py-4">
      {model.releases.map(block => (
        <div key={block.release} className="space-y-2">
          <div className="flex flex-wrap items-center gap-4">
            <h4 className="text-sm font-semibold">{releaseLabel(block.release)}</h4>
            {block.current && (
              <span className="flex items-center gap-2 text-sm text-muted-foreground">
                <Pill>{`Set ${block.current.version}`}</Pill>
                {`Current${block.current.builtAt ? `, built ${formatTimestamp(block.current.builtAt)}` : ''}`}
              </span>
            )}
            {block.rollBack && (
              <span className="flex items-center gap-2 text-sm text-muted-foreground">
                <Pill>{`Set ${block.rollBack.version}`}</Pill>
                {`Roll-back${block.rollBack.builtAt ? `, built ${formatTimestamp(block.rollBack.builtAt)}` : ''}`}
              </span>
            )}
            <span className="ml-auto text-sm text-muted-foreground">{`${block.componentCount} components, ${formatSize(block.bytes)}`}</span>
          </div>
          <div className="overflow-hidden rounded-md border border-border bg-background">
          <Table className="table-fixed">
            <TableHeader>
              <TableRow>
                <TableHead>Component</TableHead>
                <TableHead className="w-[130px]">Version</TableHead>
                <TableHead className="w-[100px]">Size</TableHead>
                <TableHead className="w-[170px]">Driver sets</TableHead>
                <TableHead className="w-[130px]">Shared with</TableHead>
                <TableHead className="w-[140px]">Status</TableHead>
                <TableHead className="w-[150px]">Downloaded</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {block.components.map(component => (
                <TableRow key={`${component.lineageKey}-${component.version ?? ''}`}>
                  <TableCell>
                    <div className="truncate font-medium" title={component.name}>{component.name}</div>
                    <div className="truncate text-xs text-muted-foreground">{component.componentId}</div>
                  </TableCell>
                  <TableCell className="truncate" title={component.version ?? undefined}>{component.version}</TableCell>
                  <TableCell>{formatSize(component.sizeBytes)}</TableCell>
                  <TableCell>
                    <span className="flex flex-wrap gap-1">{component.sets.map(set => <Pill key={set}>{setPillLabel(set)}</Pill>)}</span>
                  </TableCell>
                  <TableCell className="truncate">{sharedWithLabel(component.sharedWithModels)}</TableCell>
                  <TableCell><StoredStatus component={component} /></TableCell>
                  <TableCell>{component.downloadedAt ? formatTimestamp(component.downloadedAt) : 'Not yet'}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
          </div>
        </div>
      ))}
    </div>
  );
}

/** Storage tab: what the driver files use, how many previous versions are kept, and per model what is stored. */
export function StorageTab(): React.ReactElement {
  const { canManageDrivers } = useAuth();
  const { notify } = useToast();
  const [storage, setStorage] = useState<DriverStorage | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [open, setOpen] = useState<string | null>(null);

  const load = useCallback(async () => {
    setLoadError(null);
    try {
      setStorage(await fetchStorage());
    } catch (err) {
      setLoadError(err instanceof Error ? err.message : 'Could not load storage.');
    }
  }, []);

  useEffect(() => { void load(); }, [load]);

  if (loadError) {
    return (
      <div className="rounded-md border border-border">
        <EmptyState icon={RefreshCw} title="Could not load storage" description={loadError} action={<Button variant="outline" onClick={() => void load()}><RefreshCw />Try again</Button>} />
      </div>
    );
  }
  if (!storage) {
    return <div className="space-y-4"><Skeleton className="h-16 w-full" /><Skeleton className="h-32 w-full" /></div>;
  }

  const choose = async (value: string) => {
    setSaving(true);
    try {
      setStorage(await setPreviousVersionsToKeep(Number(value)));
      notify({ status: 'success', title: 'Previous versions setting saved.' });
    } catch (err) {
      notify({ status: 'error', title: 'Could not save the setting', description: err instanceof Error ? err.message : undefined });
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="space-y-6">
      <div className="grid grid-cols-2 gap-4 md:grid-cols-4">
        <Tile
          label={(
            <span className="inline-flex items-center gap-1">
              Total used
              <Tooltip content={TOTAL_HINT}>
                <span tabIndex={0} aria-label={TOTAL_HINT} className="inline-flex text-muted-foreground">
                  <Info className="h-3 w-3" aria-hidden="true" />
                </span>
              </Tooltip>
            </span>
          )}
          value={formatSize(storage.totalBytes)}
        />
        {storage.vendors.map(v => <Tile key={v.vendor} label={vendorLabel(v.vendor)} value={formatSize(v.bytes)} />)}
      </div>

      <Card>
        <CardContent className="py-2">
          <div className="flex items-center gap-6 py-3">
            <div className="min-w-0 flex-1">
              <p className="text-sm font-medium">Previous versions</p>
              <p className="text-sm text-muted-foreground">
                {`Earlier versions of each component kept in storage, so rolling back to one is instant and does not depend on the vendor. Older versions are downloaded again from the vendor when someone rolls back to them. Uses ${formatSize(storage.previousVersionsBytes)} now.`}
              </p>
            </div>
            <Select
              value={String(storage.previousVersionsToKeep)}
              onValueChange={value => void choose(value)}
              options={[...PREVIOUS_VERSION_OPTIONS]}
              aria-label="Previous versions"
              disabled={!canManageDrivers || saving}
              wrapperClassName="w-[240px] shrink-0"
            />
          </div>
        </CardContent>
      </Card>

      <div className="overflow-hidden rounded-md border border-border">
        <Table className="table-fixed">
          <TableHeader>
            <TableRow>
              <TableHead className="w-[44px]"><span className="sr-only">Details</span></TableHead>
              <TableHead>Model</TableHead>
              <TableHead className="w-[100px]">Vendor</TableHead>
              <TableHead className="w-[260px]">Windows releases</TableHead>
              <TableHead className="w-[180px]">Driver sets</TableHead>
              <TableHead className="w-[110px]">Size</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {storage.models.length === 0 && (
              <TableRow className="hover:bg-transparent">
                <TableCell colSpan={6} className="p-0">
                  <EmptyState icon={HardDrive} title="Nothing stored yet" description="Driver files are stored once a model's onboarding is complete." />
                </TableCell>
              </TableRow>
            )}
            {storage.models.map(model => {
              const expanded = open === model.modelId;
              const releases = model.releases.map(releaseLabel).join(', ');
              return (
                <Fragment key={model.modelId}>
                  <TableRow className="h-14">
                    <TableCell>
                      <button
                        type="button"
                        aria-expanded={expanded}
                        aria-label={`${expanded ? 'Hide' : 'Show'} stored components of ${model.displayName}`}
                        className="inline-flex rounded-md p-1 text-muted-foreground hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
                        onClick={() => setOpen(expanded ? null : model.modelId)}
                      >
                        {expanded ? <ChevronDown className="h-4 w-4" aria-hidden="true" /> : <ChevronRight className="h-4 w-4" aria-hidden="true" />}
                      </button>
                    </TableCell>
                    <TableCell className="truncate font-medium" title={model.displayName}>{model.displayName}</TableCell>
                    <TableCell>{vendorLabel(model.vendor)}</TableCell>
                    <TableCell className="truncate" title={releases}>{releases}</TableCell>
                    <TableCell>{setCountsLabel(model.sets)}</TableCell>
                    <TableCell>{formatSize(model.sizeBytes)}</TableCell>
                  </TableRow>
                  {expanded && (
                    <TableRow>
                      <TableCell colSpan={6} className="bg-muted/30 p-0"><ModelBlocks modelId={model.modelId} /></TableCell>
                    </TableRow>
                  )}
                </Fragment>
              );
            })}
          </TableBody>
        </Table>
      </div>
    </div>
  );
}
