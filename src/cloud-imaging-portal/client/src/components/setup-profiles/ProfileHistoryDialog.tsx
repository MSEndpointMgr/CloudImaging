import { Fragment, useCallback, useEffect, useState } from 'react';
import { createPortal } from 'react-dom';
import { ChevronDown, Eye, Undo2, X } from 'lucide-react';
import { Badge } from '../ui/badge.tsx';
import { Button } from '../ui/button.tsx';
import { TableSkeletonRows } from '../ui/skeleton.tsx';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '../ui/table.tsx';
import { Tooltip } from '../ui/tooltip.tsx';
import { ConfirmImpactDialog } from '../ConfirmImpactDialog.tsx';
import { cn, formatTimestamp } from '../../lib/utils.ts';
import {
  changesLabel,
  fetchSetupProfileHistory,
  fetchSetupProfileVersions,
  inUseTooltip,
  restoreImpact,
  restoreSetupProfileVersion,
  targetingHistory,
  versionFields,
  versionsFootnote,
  type SetupProfileDetail,
  type SetupProfileHistoryEntry,
  type SetupProfileVersionSummary,
} from '../../lib/setupProfiles.ts';

type Tab = 'versions' | 'targeting';

/**
 * Versions and targeting changes of one profile. Viewing opens a version read-only; Restore saves
 * an earlier version's content as a new current version without touching targeting.
 */
export function ProfileHistoryDialog({ profile, retention, canRestore, onClose, onView, onRestored }: {
  profile: Pick<SetupProfileDetail, 'profileId' | 'name' | 'currentVersion'>;
  /** Versions kept per profile, for the note under the table; null when the user cannot read it. */
  retention: number | null;
  canRestore: boolean;
  onClose: () => void;
  onView: (version: number) => void;
  onRestored: (detail: SetupProfileDetail) => void;
}): React.ReactElement {
  const [tab, setTab] = useState<Tab>('versions');
  const [versions, setVersions] = useState<SetupProfileVersionSummary[] | null>(null);
  const [history, setHistory] = useState<SetupProfileHistoryEntry[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [expanded, setExpanded] = useState<number | null>(null);
  const [restoring, setRestoring] = useState<number | null>(null);
  const [restoreBusy, setRestoreBusy] = useState(false);
  const [restoreError, setRestoreError] = useState<string | null>(null);

  const load = useCallback(async () => {
    setError(null);
    try {
      const [v, h] = await Promise.all([fetchSetupProfileVersions(profile.profileId), fetchSetupProfileHistory(profile.profileId)]);
      setVersions(v);
      setHistory(targetingHistory(h));
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not load the history.');
    }
  }, [profile.profileId]);

  useEffect(() => { void load(); }, [load]);

  const restore = async () => {
    if (restoring === null) return;
    setRestoreBusy(true);
    setRestoreError(null);
    try {
      const restored = await restoreSetupProfileVersion(profile.profileId, restoring, profile.currentVersion);
      setRestoring(null);
      onRestored(restored);
    } catch (err) {
      setRestoreError(err instanceof Error ? err.message : 'The version was not restored.');
    } finally {
      setRestoreBusy(false);
    }
  };

  const tabs: { key: Tab; label: string }[] = [{ key: 'versions', label: 'Versions' }, { key: 'targeting', label: 'Targeting' }];

  return createPortal(
    <div className="fixed inset-0 z-40 flex items-center justify-center bg-black/60 p-4" role="dialog" aria-modal="true" aria-labelledby="history-title">
      <div className="flex max-h-full w-full max-w-5xl flex-col rounded-lg border border-border bg-background shadow-xl">
        <div className="flex items-start justify-between gap-3 border-b border-border p-5">
          <div className="min-w-0 space-y-1">
            <h2 id="history-title" className="truncate text-base font-semibold">History of {profile.name}</h2>
            <p className="text-sm text-muted-foreground">Each save is a version. Restoring one saves its content as a new version.</p>
          </div>
          <Button variant="ghost" size="icon" onClick={onClose} aria-label="Close"><X /></Button>
        </div>

        <div className="space-y-4 overflow-y-auto p-5">
          <div className="border-b border-border">
            <nav className="flex gap-1" role="tablist" aria-label="History">
              {tabs.map((t) => (
                <button
                  key={t.key}
                  type="button"
                  role="tab"
                  aria-selected={tab === t.key}
                  onClick={() => setTab(t.key)}
                  className={cn(
                    '-mb-px border-b-2 px-4 py-2 text-sm font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring',
                    tab === t.key ? 'border-primary text-foreground' : 'border-transparent text-muted-foreground hover:text-foreground',
                  )}
                >
                  {t.label}
                </button>
              ))}
            </nav>
          </div>

          {error ? (
            <div className="flex items-center gap-2">
              <p className="text-sm text-destructive">{error}</p>
              <Button variant="link" onClick={() => void load()}>Try again</Button>
            </div>
          ) : tab === 'versions' ? (
            <div className="space-y-2">
              <div className="overflow-hidden rounded-md border border-border">
                <Table className="table-fixed">
                  <TableHeader>
                    <TableRow className="hover:bg-transparent">
                      <TableHead className="w-[44px]"><span className="sr-only">Details</span></TableHead>
                      <TableHead className="w-[90px] text-right">Version</TableHead>
                      <TableHead className="w-[160px]">Modified</TableHead>
                      <TableHead className="w-[140px]">Modified by</TableHead>
                      <TableHead>Changes</TableHead>
                      <TableHead className="w-[120px]">Status</TableHead>
                      <TableHead className="w-[96px]">Actions</TableHead>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {versions === null ? (
                      <TableSkeletonRows columns={7} rows={3} />
                    ) : versions.map((v) => {
                      const open = expanded === v.version;
                      const detailsId = `version-${String(v.version)}-details`;
                      return (
                        <Fragment key={v.version}>
                          <TableRow>
                            <TableCell>
                              <Button variant="ghost" size="icon" className="text-muted-foreground" aria-label={open ? 'Hide changes' : 'Show changes'} aria-expanded={open} aria-controls={detailsId}
                                onClick={() => setExpanded(open ? null : v.version)}>
                                <ChevronDown className={cn('transition-transform', open && 'rotate-180')} />
                              </Button>
                            </TableCell>
                            <TableCell className="text-right tabular-nums">{v.version}</TableCell>
                            <TableCell className="tabular-nums">{formatTimestamp(v.createdAt)}</TableCell>
                            <TableCell><p className="truncate">{v.createdBy ?? 'Unknown'}</p></TableCell>
                            <TableCell>{changesLabel(v.changeSummary.total)}</TableCell>
                            <TableCell>
                              {v.isCurrent ? (
                                <Badge variant="success"><span className="mr-1.5 h-1.5 w-1.5 rounded-full bg-current" aria-hidden="true" />Current</Badge>
                              ) : v.inUseCount > 0 ? (
                                <Tooltip content={inUseTooltip(v.inUseCount)}>
                                  <Badge variant="muted" tabIndex={0}><span className="mr-1.5 h-1.5 w-1.5 rounded-full bg-current" aria-hidden="true" />In use</Badge>
                                </Tooltip>
                              ) : null}
                            </TableCell>
                            <TableCell>
                              <div className="flex gap-1">
                                <Tooltip content="View">
                                  <Button variant="ghost" size="icon" aria-label={`View version ${String(v.version)}`} onClick={() => onView(v.version)}><Eye /></Button>
                                </Tooltip>
                                {canRestore && !v.isCurrent && (
                                  <Tooltip content="Restore">
                                    <Button variant="ghost" size="icon" className="text-muted-foreground" aria-label={`Restore version ${String(v.version)}`} onClick={() => setRestoring(v.version)}><Undo2 /></Button>
                                  </Tooltip>
                                )}
                              </div>
                            </TableCell>
                          </TableRow>
                          {open && (
                            <TableRow id={detailsId} className="hover:bg-transparent">
                              <TableCell colSpan={7} className="bg-muted/20">
                                <dl className="grid grid-cols-2 gap-x-6 gap-y-3 py-1 pl-14 text-sm">
                                  {versionFields(v).map((f) => (
                                    <div key={f.label} className="min-w-0">
                                      <dt className="text-xs text-muted-foreground">{f.label}</dt>
                                      <dd className={cn('break-words', f.value === 'No change' && 'text-muted-foreground')}>{f.value}</dd>
                                    </div>
                                  ))}
                                </dl>
                              </TableCell>
                            </TableRow>
                          )}
                        </Fragment>
                      );
                    })}
                  </TableBody>
                </Table>
              </div>
              {versions && <p className="text-xs text-muted-foreground">{versionsFootnote(versions.length, retention)}</p>}
            </div>
          ) : (
            <div className="overflow-hidden rounded-md border border-border">
              <Table className="table-fixed">
                <TableHeader>
                  <TableRow className="hover:bg-transparent">
                    <TableHead className="w-[160px]">Modified</TableHead>
                    <TableHead className="w-[140px]">Modified by</TableHead>
                    <TableHead>Change</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {history === null ? (
                    <TableSkeletonRows columns={3} rows={2} />
                  ) : history.length === 0 ? (
                    <TableRow className="hover:bg-transparent"><TableCell colSpan={3} className="text-muted-foreground">No targeting changes.</TableCell></TableRow>
                  ) : history.map((h, i) => (
                    <TableRow key={`${h.at}-${String(i)}`}>
                      <TableCell className="tabular-nums">{formatTimestamp(h.at)}</TableCell>
                      <TableCell><p className="truncate">{h.by ?? 'Unknown'}</p></TableCell>
                      <TableCell>
                        <Tooltip content={h.change} className="min-w-0 max-w-full">
                          <span tabIndex={0} className="block truncate">{h.change}</span>
                        </Tooltip>
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </div>
          )}
        </div>

        <div className="flex justify-end border-t border-border px-5 py-3">
          <Button variant="outline" onClick={onClose}>Close</Button>
        </div>
      </div>

      {restoring !== null && (
        <ConfirmImpactDialog
          icon={Undo2}
          copy={{ confirmTitle: `Restore version ${String(restoring)}?`, impact: restoreImpact(restoring, profile.currentVersion), confirmLabel: 'Restore', destructive: false }}
          busy={restoreBusy}
          onCancel={() => { setRestoring(null); setRestoreError(null); }}
          onConfirm={() => void restore()}
          titleId="restore-version-title"
          details={restoreError ? <p className="text-sm text-destructive">{restoreError}</p> : undefined}
        />
      )}
    </div>,
    document.body,
  );
}
