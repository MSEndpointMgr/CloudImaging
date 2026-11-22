import React, { Fragment, useCallback, useEffect, useId, useState } from 'react';
import { ChevronDown, ChevronRight, CircleX, Cpu, Download, ExternalLink, FlaskConical, Info, RefreshCw, Rocket, Undo2 } from 'lucide-react';
import { ConfirmImpactDialog } from '../ConfirmImpactDialog.tsx';
import { Button } from '../ui/button.tsx';
import { EmptyState } from '../ui/empty-state.tsx';
import { Select } from '../ui/select.tsx';
import { Skeleton } from '../ui/skeleton.tsx';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '../ui/table.tsx';
import { Tooltip } from '../ui/tooltip.tsx';
import { useToast } from '../../context/toastContext.tsx';
import {
  FIRMWARE_ACTIONS, fetchFirmware, fetchFirmwareVersions, firmwareActionLabel, formatSize, releaseFirmware, rollBackFirmware, rollBackText, setFirmwareAction, undoFirmwareRollBack,
  type DriverComponentVersion, type DriverFirmwareAction, type DriverFirmwarePage, type DriverFirmwareRow, type DriverRollBackUntil, type DriverVendor,
} from '../../lib/drivers.ts';
import { Banner } from './Banner.tsx';
import { VersionHistoryBox } from './ComponentDetails.tsx';

const ACTION_HINT = 'Update at restart: the BIOS is staged at the end of imaging and flashes at the restart into Windows setup. Devices that already have this version or a newer one are left alone. Skip: the BIOS is left as it is.';

/** How often the tab is read again while a BIOS downloads. */
export const FIRMWARE_POLL_MS = 15_000;

function SourceText({ row, vendor, saving, onUndoOverride }: { row: DriverFirmwareRow; vendor: DriverVendor; saving: boolean; onUndoOverride?: () => void }): React.ReactElement {
  if (row.source === 'RolledBack' && row.rollBack) {
    const hint = rollBackText(row.rollBack, vendor);
    return (
      <span className="flex items-center gap-2">
        <Tooltip content={hint}>
          <span tabIndex={0} aria-label={hint} className="inline-flex shrink-0">
            <Undo2 className="h-4 w-4 text-amber-600 dark:text-amber-400" aria-hidden="true" />
          </span>
        </Tooltip>
        <span>Rolled back</span>
      </span>
    );
  }
  if (row.source === 'Override') {
    return (
      <span className="flex items-center gap-2">
        <span>Override</span>
        {onUndoOverride && (
          <Tooltip content="Undo override">
            <Button variant="ghost" size="icon" aria-label={`Undo override of ${row.name}`} disabled={saving} onClick={onUndoOverride}><Undo2 /></Button>
          </Tooltip>
        )}
      </span>
    );
  }
  return <span className="text-muted-foreground">Default</span>;
}

/** Roll back the BIOS: choose an earlier version and how long every location keeps it. */
function RollBackFirmwareDialog({ modelId, row, onCancel, onDone }: { modelId: string; row: DriverFirmwareRow; onCancel: () => void; onDone: (page: DriverFirmwarePage) => void }): React.ReactElement {
  const untilName = useId();
  const [versions, setVersions] = useState<DriverComponentVersion[] | null>(null);
  const [version, setVersion] = useState('');
  const [until, setUntil] = useState<DriverRollBackUntil>('NewerVersion');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let current = true;
    fetchFirmwareVersions(modelId, row.platformId)
      .then(list => {
        if (!current) return;
        const earlier = list.filter(v => v.version !== row.releasedVersion);
        setVersions(earlier);
        setVersion(earlier.find(v => v.status !== 'Unavailable')?.version ?? '');
      })
      .catch((err: unknown) => { if (current) { setVersions([]); setError(err instanceof Error ? err.message : 'Could not load the version history.'); } });
    return () => { current = false; };
  }, [modelId, row.platformId, row.releasedVersion]);

  const chosen = versions?.find(v => v.version === version);
  const confirm = async () => {
    setBusy(true);
    setError(null);
    try {
      onDone(await rollBackFirmware(modelId, row.platformId, version, until));
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not roll back the BIOS.');
    } finally {
      setBusy(false);
    }
  };

  const details = (
    <div className="space-y-4 pt-2">
      <div className="space-y-1">
        <label className="text-sm font-medium" htmlFor={`${untilName}-version`}>Version</label>
        <Select
          id={`${untilName}-version`}
          value={version}
          onValueChange={setVersion}
          options={(versions ?? []).map(v => ({
            value: v.version,
            label: v.version,
            description: v.status === 'Stored' ? 'Stored' : v.status === 'DownloadsOnRollBack' ? 'Downloads on roll back' : 'No longer available from HP',
            disabled: v.status === 'Unavailable',
          }))}
          placeholder={versions === null ? 'Loading versions' : 'No earlier version'}
          disabled={versions === null || busy}
          aria-label="Version"
        />
      </div>
      <fieldset className="space-y-2">
        <legend className="text-sm font-medium">Until</legend>
        <label className="flex items-start gap-2 text-sm">
          <input type="radio" name={untilName} className="mt-1" checked={until === 'NewerVersion'} onChange={() => setUntil('NewerVersion')} disabled={busy} />
          <span>
            <span className="block">{`HP publishes a version newer than ${row.rollBack?.skippedVersion ?? row.version}`}</span>
            <span className="block text-sm text-muted-foreground">The BIOS then moves forward to that version on its own.</span>
          </span>
        </label>
        <label className="flex items-start gap-2 text-sm">
          <input type="radio" name={untilName} className="mt-1" checked={until === 'Undone'} onChange={() => setUntil('Undone')} disabled={busy} />
          <span>I undo the roll back</span>
        </label>
      </fieldset>
      {chosen && chosen.status !== 'Stored' && (
        <p className="text-sm text-muted-foreground">This version is no longer stored, so it is downloaded again from HP and checked against HP&apos;s checksum before devices get it.</p>
      )}
      {error && <p role="alert" className="text-sm text-destructive">{error}</p>}
    </div>
  );

  return (
    <ConfirmImpactDialog
      copy={{
        confirmTitle: `Roll back ${row.name}?`,
        impact: 'Devices of this model imaged from now on that have an older BIOS get the version you choose. Devices never go back to an older BIOS than the one they have. Running deployments keep the version they started with.',
        confirmLabel: 'Roll back',
        destructive: false,
      }}
      icon={Undo2}
      busy={busy}
      onCancel={onCancel}
      onConfirm={() => void confirm()}
      titleId="rollback-firmware-title"
      details={details}
      confirmDisabled={!chosen}
    />
  );
}

/**
 * The Firmware tab of a model: one System BIOS row per HP platform, laid out like the driver
 * components, with the pre-production banner above. The BIOS is the same for every Windows
 * release, so there is no release to choose.
 */
export function FirmwareTab({ modelId, vendor, canManage }: { modelId: string; vendor: DriverVendor; canManage: boolean }): React.ReactElement {
  const { notify } = useToast();
  const [page, setPage] = useState<DriverFirmwarePage | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [open, setOpen] = useState<string | null>(null);
  const [saving, setSaving] = useState<string | null>(null);
  const [releasing, setReleasing] = useState<string | null>(null);
  const [rollingBack, setRollingBack] = useState<DriverFirmwareRow | null>(null);
  const [undoing, setUndoing] = useState<DriverFirmwareRow | null>(null);
  const [confirming, setConfirming] = useState(false);

  const load = useCallback(async () => {
    setLoadError(null);
    try {
      setPage(await fetchFirmware(modelId));
    } catch (err) {
      setLoadError(err instanceof Error ? err.message : 'Could not load the firmware.');
    }
  }, [modelId]);

  useEffect(() => { void load(); }, [load]);

  // A BIOS downloads in the background after Update at restart, so the tab follows it until it settles.
  const downloading = page?.rows.some(r => r.downloading) ?? false;
  useEffect(() => {
    if (!downloading) return undefined;
    const timer = window.setInterval(() => void load(), FIRMWARE_POLL_MS);
    return () => window.clearInterval(timer);
  }, [downloading, load]);

  if (loadError) {
    return (
      <div className="rounded-md border border-border">
        <EmptyState icon={RefreshCw} title="Could not load the firmware" description={loadError} action={<Button variant="outline" onClick={() => void load()}><RefreshCw />Try again</Button>} />
      </div>
    );
  }
  if (!page) return <Skeleton className="h-40 w-full" />;
  if (!page.supported) {
    return (
      <div className="rounded-md border border-border">
        <EmptyState icon={Cpu} title="No BIOS updates for this model" description="Cloud Imaging updates the BIOS of HP x64 models." />
      </div>
    );
  }

  const changeAction = async (row: DriverFirmwareRow, action: DriverFirmwareAction) => {
    setSaving(row.platformId);
    try {
      setPage(await setFirmwareAction(modelId, row.platformId, action));
    } catch (err) {
      notify({ status: 'error', title: 'Could not change the action', description: err instanceof Error ? err.message : undefined });
    } finally {
      setSaving(null);
    }
  };

  // Releasing needs no confirmation, as with a driver set in testing: it is what pre-production is for.
  const release = async (platformId: string, version: string) => {
    setReleasing(platformId);
    try {
      setPage(await releaseFirmware(modelId, platformId));
      notify({ status: 'success', title: `BIOS ${version} released.`, description: 'Devices of every location get it from their next deployment.' });
    } catch (err) {
      notify({ status: 'error', title: 'Could not release the BIOS', description: err instanceof Error ? err.message : undefined });
    } finally {
      setReleasing(null);
    }
  };

  const undoRollBack = async () => {
    if (!undoing) return;
    setConfirming(true);
    try {
      setPage(await undoFirmwareRollBack(modelId, undoing.platformId));
      notify({ status: 'success', title: `${undoing.name} follows HP again.` });
      setUndoing(null);
    } catch (err) {
      notify({ status: 'error', title: 'The change was not made', description: err instanceof Error ? err.message : undefined });
    } finally {
      setConfirming(false);
    }
  };

  const several = page.rows.length > 1;
  const nameOf = (row: DriverFirmwareRow) => (several ? `${row.name} (${row.platformId})` : row.name);
  const location = page.preProductionLocationName ?? 'the pre-production location';

  return (
    <div className="space-y-6">
      {page.preProduction.map(pre => (
        <Banner
          key={pre.platformId}
          tone="info"
          icon={<FlaskConical className="h-5 w-5" aria-hidden="true" />}
          title={`BIOS ${pre.version}${pre.releasedAt ? ` (${pre.releasedAt})` : ''}${several ? ` for ${pre.platformId}` : ''}`}
          detail={`In pre-production at ${location}. Other locations get ${pre.releasedVersion}.`}
        >
          {canManage && <Button onClick={() => void release(pre.platformId, pre.version)} loading={releasing === pre.platformId}><Rocket />Release</Button>}
        </Banner>
      ))}

      <div className="overflow-hidden rounded-md border border-border">
        <Table className="table-fixed">
          <TableHeader>
            <TableRow>
              <TableHead className="w-[44px]"><span className="sr-only">Details</span></TableHead>
              <TableHead>Component</TableHead>
              <TableHead className="w-[128px]">Version</TableHead>
              <TableHead className="w-[132px] whitespace-nowrap">Last updated</TableHead>
              <TableHead className="w-[224px]">
                <span className="inline-flex items-center gap-1">
                  Action
                  <Tooltip content={ACTION_HINT}>
                    <span tabIndex={0} aria-label={ACTION_HINT} className="inline-flex text-muted-foreground">
                      <Info className="h-3 w-3" aria-hidden="true" />
                    </span>
                  </Tooltip>
                </span>
              </TableHead>
              <TableHead className="w-[160px]">Source</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {page.rows.length === 0 && (
              <TableRow className="hover:bg-transparent">
                <TableCell colSpan={6} className="p-0">
                  <EmptyState icon={Cpu} title="No BIOS found yet" description="The next sync or Check for updates reads HP's BIOS catalog for this model." />
                </TableCell>
              </TableRow>
            )}
            {page.rows.map(row => {
              const expanded = open === row.platformId;
              const detailsId = `firmware-details-${row.platformId}`;
              const name = nameOf(row);
              return (
                <Fragment key={row.platformId}>
                  <TableRow className={`h-14 ${row.action === 'Skip' ? 'text-muted-foreground' : ''}`}>
                    <TableCell>
                      <button
                        type="button"
                        aria-expanded={expanded}
                        aria-controls={detailsId}
                        aria-label={`${expanded ? 'Hide' : 'Show'} details of ${name}`}
                        className="inline-flex rounded-md p-1 text-muted-foreground hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
                        onClick={() => setOpen(expanded ? null : row.platformId)}
                      >
                        {expanded ? <ChevronDown className="h-4 w-4" aria-hidden="true" /> : <ChevronRight className="h-4 w-4" aria-hidden="true" />}
                      </button>
                    </TableCell>
                    <TableCell>
                      <div className="flex min-w-0 items-center gap-2">
                        <span className="truncate font-medium" title={name}>{name}</span>
                        {row.downloadFailure && (
                          <Tooltip content={row.downloadFailure}>
                            <span tabIndex={0} aria-label={row.downloadFailure} className="inline-flex shrink-0">
                              <CircleX className="h-4 w-4 text-destructive" aria-hidden="true" />
                            </span>
                          </Tooltip>
                        )}
                        {!row.downloadFailure && row.downloading && (
                          <Tooltip content="Downloading. Devices get it once it is downloaded and verified.">
                            <span tabIndex={0} aria-label="Downloading" className="inline-flex shrink-0">
                              <Download className="h-4 w-4 text-blue-600 dark:text-blue-400" aria-hidden="true" />
                            </span>
                          </Tooltip>
                        )}
                      </div>
                      <div className="flex min-w-0 items-center gap-2 text-xs text-muted-foreground">
                        <span className="truncate font-mono">{row.fileName}</span>
                        <span className="shrink-0">Firmware</span>
                      </div>
                    </TableCell>
                    <TableCell className="truncate" title={row.version}>{row.version}</TableCell>
                    <TableCell>{row.lastUpdated}</TableCell>
                    <TableCell>
                      {canManage ? (
                        <Select
                          value={row.action}
                          onValueChange={value => { if (value !== row.action) void changeAction(row, value as DriverFirmwareAction); }}
                          options={FIRMWARE_ACTIONS.map(a => ({ value: a, label: firmwareActionLabel(a) }))}
                          disabled={saving === row.platformId}
                          aria-label={`Action for ${name}`}
                          wrapperClassName="w-[176px] shrink-0"
                        />
                      ) : firmwareActionLabel(row.action)}
                    </TableCell>
                    <TableCell>
                      <SourceText row={row} vendor={vendor} saving={saving === row.platformId} onUndoOverride={canManage ? () => void changeAction(row, 'Skip') : undefined} />
                    </TableCell>
                  </TableRow>
                  {expanded && (
                    <TableRow id={detailsId}>
                      <TableCell colSpan={6} className="bg-muted/30 p-0">
                        <div className="space-y-4 px-4 py-4">
                          <dl className="grid grid-cols-3 gap-x-8 gap-y-3">
                            {[
                              { label: 'Platform ID', value: row.platformId },
                              { label: 'Criticality', value: row.critical ? 'Critical' : 'Recommended' },
                              { label: 'Size', value: row.sizeBytes > 0 ? formatSize(row.sizeBytes) : 'Not downloaded' },
                              { label: 'File', value: row.fileName },
                              { label: 'Released', value: row.lastUpdated ?? 'Unknown' },
                            ].map(field => (
                              <div key={field.label} className="min-w-0">
                                <dt className="text-xs text-muted-foreground">{field.label}</dt>
                                <dd className="truncate text-sm" title={field.value}>{field.value}</dd>
                              </div>
                            ))}
                          </dl>
                          {row.descriptionUrl && (
                            <div className="rounded-md border border-border px-4 py-3">
                              <div className="text-xs text-muted-foreground">Description</div>
                              <a href={row.descriptionUrl} target="_blank" rel="noopener noreferrer" className="inline-flex items-center gap-1 text-sm underline-offset-4 hover:underline">
                                Release notes
                                <ExternalLink className="h-3 w-3" aria-hidden="true" />
                              </a>
                            </div>
                          )}
                          {row.downloadFailure && (
                            <p role="alert" className="text-sm text-destructive">{`${row.downloadFailure} The next sync tries again; until then devices are not updated.`}</p>
                          )}
                          <VersionHistoryBox
                            source={{ kind: 'firmware', modelId, platformId: row.platformId, version: row.releasedVersion }}
                            vendor={vendor}
                            rolledBack={!!row.rollBack}
                            onRollBack={canManage ? () => setRollingBack(row) : undefined}
                            onUndoRollBack={canManage ? () => setUndoing(row) : undefined}
                          />
                        </div>
                      </TableCell>
                    </TableRow>
                  )}
                </Fragment>
              );
            })}
          </TableBody>
        </Table>
        <div className="flex items-center gap-4 border-t border-border bg-muted/30 px-3 py-2 text-xs text-muted-foreground">
          <span>{page.rows.length === 1 ? '1 component' : `${page.rows.length} components`}</span>
          {page.newVersions === 'PreProductionFirst' && <span>{`Newer versions go to ${location} first.`}</span>}
        </div>
      </div>

      {rollingBack && (
        <RollBackFirmwareDialog
          modelId={modelId}
          row={rollingBack}
          onCancel={() => setRollingBack(null)}
          onDone={next => {
            setRollingBack(null);
            setPage(next);
            notify({ status: 'success', title: `${rollingBack.name} rolled back.` });
          }}
        />
      )}

      {undoing && (
        <ConfirmImpactDialog
          copy={{
            confirmTitle: `Undo the roll back of ${undoing.name}?`,
            impact: `Devices imaged from now on get ${undoing.version}, the version HP publishes now, instead of ${undoing.rollBack?.version ?? undoing.releasedVersion}.`,
            confirmLabel: 'Undo roll back',
            destructive: false,
          }}
          icon={Undo2}
          busy={confirming}
          onCancel={() => setUndoing(null)}
          onConfirm={() => void undoRollBack()}
          titleId="undo-firmware-rollback-title"
        />
      )}
      {page.rows.some(r => r.downloading) && <p className="sr-only" aria-live="polite">Downloading the BIOS.</p>}
    </div>
  );
}
