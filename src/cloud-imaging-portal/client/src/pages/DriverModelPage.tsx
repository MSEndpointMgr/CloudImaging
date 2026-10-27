import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Link, useNavigate, useParams, useSearchParams } from 'react-router-dom';
import { ArrowRight, Check, ChevronRight, CircleAlert, CircleDashed, Download, Hammer, History, Plus, RefreshCw, Trash2, Undo2, X } from 'lucide-react';
import { ConfirmImpactDialog, type ConfirmImpactCopy } from '../components/ConfirmImpactDialog.tsx';
import { Badge } from '../components/ui/badge.tsx';
import { Button } from '../components/ui/button.tsx';
import { EmptyState } from '../components/ui/empty-state.tsx';
import { SearchInput } from '../components/ui/search-input.tsx';
import { Select } from '../components/ui/select.tsx';
import { Skeleton } from '../components/ui/skeleton.tsx';
import { Tooltip } from '../components/ui/tooltip.tsx';
import { ComponentTable } from '../components/drivers/ComponentTable.tsx';
import { RollBackComponentDialog } from '../components/drivers/RollBackComponentDialog.tsx';
import { AddCustomComponentDialog } from '../components/drivers/AddCustomComponentDialog.tsx';
import { SizeHead } from '../components/drivers/ModelsTable.tsx';
import { useAuth } from '../context/authContext.tsx';
import { useToast } from '../context/toastContext.tsx';
import { formatTimestamp } from '../lib/utils.ts';
import {
  DRIVER_ACTIONS, WINDOWS_RELEASES, actionLabel, addRelease, approveComponents, buildNow, checkModel, clearOverride, completeOnboarding, fetchModelPage, fetchRemovalImpact, formatSize, matchKeyLabel,
  releaseLabel, removeModel, removeRelease, removeWithdrawnComponent, rollBackSet, setOverride, undoRollBack, vendorLabel,
  deleteCustomComponent, dismissCustomNotice, fetchCustomComponents, fetchModels,
  type DriverCustomComponent, type DriverModelRow,
  type DriverAction, type DriverComponentRow, type DriverModelPage as ModelPage, type DriverRemovalImpact, type WindowsRelease,
} from '../lib/drivers.ts';

const ALL = 'all';

/** How often the page is read again while a build, download or check is under way. */
export const MODEL_POLL_MS = 15_000;

/** The overlay open on the page, if any. */
type Pending =
  | { kind: 'rollback' }
  | { kind: 'removeModel' }
  | { kind: 'removeRelease' }
  | { kind: 'removeComponent'; component: DriverComponentRow }
  | { kind: 'removeCustom'; customId: string; name: string }
  | { kind: 'undoRollBack'; component: DriverComponentRow };

/** Which models return to which vendor component when a custom component is removed. */
function CustomRemovalDetails({ targets, vendor, failed, onRetry }: { targets: DriverCustomComponent['targets'] | null; vendor: string; failed: boolean; onRetry: () => void }): React.ReactElement {
  if (failed) {
    return (
      <div className="flex items-center gap-2 text-sm">
        <span className="text-destructive">The models it is on could not be loaded.</span>
        <Button variant="link" onClick={onRetry}>Try again</Button>
      </div>
    );
  }
  if (!targets) return <Skeleton className="h-12 w-full" />;
  return (
    <ul className="space-y-1 text-sm">
      {targets.map(t => (
        <li key={t.modelId}>{t.replacesName ? `${t.modelName ?? 'A model'} returns to ${t.replacesName} from ${vendor}` : `${t.modelName ?? 'A model'} no longer gets it`}</li>
      ))}
    </ul>
  );
}

/** What a removal deletes, frees and keeps, as the overlay lists it. */
function RemovalDetails({ impact }: { impact: DriverRemovalImpact | null }): React.ReactElement {
  if (!impact) return <Skeleton className="h-12 w-full" />;
  const sets = impact.sets === 1 ? '1 driver set' : `${impact.sets} driver sets`;
  const overrides = impact.overrides === 1 ? '1 override' : `${impact.overrides} overrides`;
  return (
    <ul className="space-y-1 text-sm">
      <li>{`Deleted: ${sets}, ${overrides}`}</li>
      <li>{`Freed: ${formatSize(impact.freedBytes)} at the next clean-up`}</li>
      <li>Kept: rules and sync history</li>
    </ul>
  );
}

/** Why Check for updates is unavailable, as its tooltip says it; null when it can run. */
function checkBlockedText(page: ModelPage, now: number): string | null {
  switch (page.checkBlockedReason) {
    case 'Draft': return 'Available once onboarding is complete';
    case 'SyncOff': return `Sync is off for ${vendorLabel(page.model.vendor)}.`;
    case 'Running': return 'A sync is running.';
    default: return page.checkAvailableAt && Date.parse(page.checkAvailableAt) > now ? `Available at ${formatTimestamp(page.checkAvailableAt).slice(11)}` : null;
  }
}

type BannerTone = 'neutral' | 'warning' | 'info';

const BANNER_TONES: Record<BannerTone, { box: string; icon: string }> = {
  neutral: { box: 'border-border bg-muted/30', icon: 'text-muted-foreground' },
  warning: { box: 'border-amber-300 bg-amber-50 dark:border-amber-900 dark:bg-amber-950/40', icon: 'text-amber-600 dark:text-amber-400' },
  info: { box: 'border-blue-200 bg-blue-50 dark:border-blue-900 dark:bg-blue-950/40', icon: 'text-blue-600 dark:text-blue-400' },
};

/** A banner under the header: icon centred on its text, actions on the right. */
function Banner({ icon, title, detail, tone = 'neutral', children }: { icon: React.ReactNode; title: string; detail: string; tone?: BannerTone; children?: React.ReactNode }): React.ReactElement {
  return (
    <div role="status" className={`flex items-center gap-4 rounded-lg border px-4 py-3 ${BANNER_TONES[tone].box}`}>
      <span className={`shrink-0 ${BANNER_TONES[tone].icon}`}>{icon}</span>
      <div className="min-w-0 flex-1">
        <p className="text-sm font-medium">{title}</p>
        <p className="text-sm text-muted-foreground">{detail}</p>
      </div>
      {children}
    </div>
  );
}

function Tile({ label, value }: { label: React.ReactNode; value: string }): React.ReactElement {
  return (
    <div className="rounded-lg border border-border bg-card px-4 py-3">
      <div className="text-xs text-muted-foreground">{label}</div>
      <div className="text-2xl font-semibold">{value}</div>
    </div>
  );
}

/** One onboarded model for one Windows release: state banners, tiles and its components. */
export default function DriverModelPage(): React.ReactElement {
  const { modelId = '' } = useParams();
  const navigate = useNavigate();
  const [searchParams, setSearchParams] = useSearchParams();
  const release = (searchParams.get('release') ?? undefined) as WindowsRelease | undefined;
  const { canManageDrivers } = useAuth();
  const { notify } = useToast();
  const [page, setPage] = useState<ModelPage | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [busy, setBusy] = useState<'complete' | 'check' | 'build' | null>(null);
  const [saving, setSaving] = useState<string | null>(null);
  const [search, setSearch] = useState('');
  const [action, setAction] = useState<string>(ALL);
  const [category, setCategory] = useState<string>(ALL);
  const [updatedOnly, setUpdatedOnly] = useState(false);
  const [confirm, setConfirm] = useState<Pending | null>(null);
  const [impact, setImpact] = useState<DriverRemovalImpact | null>(null);
  const [confirming, setConfirming] = useState(false);
  const [adding, setAdding] = useState(false);
  const [rollingBack, setRollingBack] = useState<DriverComponentRow | null>(null);
  const [customTargets, setCustomTargets] = useState<DriverCustomComponent['targets'] | null>(null);
  const [customTargetsFailed, setCustomTargetsFailed] = useState(false);
  const [customModels, setCustomModels] = useState<DriverModelRow[] | null>(null);
  const [now, setNow] = useState(() => Date.now());
  const request = useRef(0);

  // Only the latest read is applied, so a slow answer for another release never replaces the current one.
  const load = useCallback(async () => {
    const id = ++request.current;
    setLoadError(null);
    try {
      const next = await fetchModelPage(modelId, release);
      if (id === request.current) {
        setPage(next);
        setNow(Date.now());
      }
    } catch (err) {
      if (id === request.current) setLoadError(err instanceof Error ? err.message : 'Could not load the model.');
    }
  }, [modelId, release]);

  useEffect(() => { void load(); }, [load]);

  // Builds, downloads and checks finish in the background, so the page follows them until they settle.
  const active = !!page && (!!page.pending || !!page.preparing || page.checkBlockedReason === 'Running');
  useEffect(() => {
    if (!active) return undefined;
    const timer = window.setInterval(() => void load(), MODEL_POLL_MS);
    return () => window.clearInterval(timer);
  }, [active, load]);

  // Check for updates becomes available at its time without a reload.
  const availableAt = page?.checkAvailableAt ? Date.parse(page.checkAvailableAt) : NaN;
  useEffect(() => {
    if (!Number.isFinite(availableAt) || availableAt <= now) return undefined;
    const timer = window.setTimeout(() => setNow(Date.now()), Math.min(availableAt - now + 500, 2_147_000_000));
    return () => window.clearTimeout(timer);
  }, [availableAt, now]);

  const components = useMemo(() => {
    const term = search.trim().toLowerCase();
    return (page?.components ?? []).filter(c =>
      (!term || c.name.toLowerCase().includes(term) || c.componentId.toLowerCase().includes(term))
      && (action === ALL || c.action === action)
      && (category === ALL || c.category === category)
      && (!updatedOnly || c.updatedByLastSync));
  }, [page, search, action, category, updatedOnly]);

  if (loadError) {
    return <EmptyState icon={RefreshCw} title="Could not load the model" description={loadError} action={<Button variant="outline" onClick={() => void load()}><RefreshCw />Try again</Button>} />;
  }
  if (!page) {
    return <div className="space-y-4"><Skeleton className="h-8 w-80" /><Skeleton className="h-24 w-full" /><Skeleton className="h-64 w-full" /></div>;
  }

  const { model, tiles } = page;
  const blocked = checkBlockedText(page, now);
  const categories = [...new Set(page.components.map(c => c.category).filter((c): c is string => !!c))].sort();
  const updatedCount = page.components.filter(c => c.updatedByLastSync).length;

  const complete = async () => {
    setBusy('complete');
    try {
      setPage(await completeOnboarding(model.modelId));
    } catch (err) {
      notify({ status: 'error', title: 'Could not complete onboarding', description: err instanceof Error ? err.message : undefined });
    } finally {
      setBusy(null);
    }
  };

  const check = async () => {
    setBusy('check');
    try {
      await checkModel(model.modelId);
      notify({ status: 'success', title: `Checking ${model.displayName} for updates.` });
      await load();
    } catch (err) {
      notify({ status: 'error', title: 'Could not check for updates', description: err instanceof Error ? err.message : undefined });
    } finally {
      setBusy(null);
    }
  };

  const build = async () => {
    setBusy('build');
    try {
      await buildNow(model.modelId, page.release);
      await load();
    } catch (err) {
      notify({ status: 'error', title: 'Could not build the driver set', description: err instanceof Error ? err.message : undefined });
    } finally {
      setBusy(null);
    }
  };

  // The page is read again so the tiles, Source and the waiting changes banner all follow the change.
  const change = async (component: DriverComponentRow, run: () => Promise<unknown>, failure: string) => {
    setSaving(component.lineageKey);
    try {
      await run();
      await load();
    } catch (err) {
      notify({ status: 'error', title: failure, description: err instanceof Error ? err.message : undefined });
    } finally {
      setSaving(null);
    }
  };

  const editing = canManageDrivers ? {
    saving,
    onOverride: (component: DriverComponentRow, next: DriverAction) =>
      void change(component, () => setOverride(model.modelId, page.release, component.lineageKey, next), 'Could not change the action'),
    onUndoOverride: (component: DriverComponentRow) =>
      void change(component, () => clearOverride(model.modelId, page.release, component.lineageKey), 'Could not undo the override'),
    onApprove: (component: DriverComponentRow) =>
      void change(component, () => approveComponents(model.modelId, page.release, [component.lineageKey]), 'Could not approve the component'),
    onRemoveWithdrawn: (component: DriverComponentRow) => setConfirm({ kind: 'removeComponent', component }),
    onRemoveCustom: (component: DriverComponentRow) => component.custom && open({ kind: 'removeCustom', customId: component.custom.customId, name: component.name }),
  } : undefined;

  // A removal overlay shows what it deletes and frees, read before anything is removed.
  const loadCustomTargets = (customId: string) => {
    setCustomTargets(null);
    setCustomTargetsFailed(false);
    fetchCustomComponents()
      .then(list => setCustomTargets(list.find(c => c.customId === customId)?.targets ?? []))
      .catch(() => setCustomTargetsFailed(true));
  };

  const open = (next: Pending) => {
    setConfirm(next);
    setImpact(null);
    setCustomTargets(null);
    setCustomTargetsFailed(false);
    if (next.kind === 'removeCustom') loadCustomTargets(next.customId);
    if (next.kind === 'removeModel' || next.kind === 'removeRelease') {
      fetchRemovalImpact(model.modelId, next.kind === 'removeRelease' ? page.release : undefined)
        .then(setImpact)
        .catch((err: unknown) => {
          setConfirm(null);
          notify({ status: 'error', title: 'Could not work out what the removal affects', description: err instanceof Error ? err.message : undefined });
        });
    }
  };

  const run = async () => {
    if (!confirm) return;
    setConfirming(true);
    try {
      switch (confirm.kind) {
        case 'rollback':
          setPage(await rollBackSet(model.modelId, page.release));
          notify({ status: 'success', title: `Rolled back to driver set ${page.previousSetVersion}.` });
          break;
        case 'removeModel':
          await removeModel(model.modelId);
          notify({ status: 'success', title: `${model.displayName} removed.` });
          void navigate('/drivers?tab=models');
          return;
        case 'removeRelease': {
          await removeRelease(model.modelId, page.release);
          notify({ status: 'success', title: `${releaseLabel(page.release)} removed from ${model.displayName}.` });
          const remaining = model.releases.find(r => r !== page.release);
          setConfirm(null);
          if (remaining) setSearchParams({ release: remaining }, { replace: true });
          return;
        }
        case 'removeComponent':
          await removeWithdrawnComponent(model.modelId, page.release, confirm.component.lineageKey);
          notify({ status: 'success', title: `${confirm.component.name} removed.` });
          await load();
          break;
        case 'removeCustom':
          await deleteCustomComponent(confirm.customId);
          notify({ status: 'success', title: `${confirm.name} removed.` });
          await load();
          break;
        case 'undoRollBack':
          await undoRollBack(model.modelId, page.release, confirm.component.lineageKey);
          notify({ status: 'success', title: `${confirm.component.name} follows ${vendorLabel(model.vendor)} again.` });
          await load();
          break;
      }
      setConfirm(null);
    } catch (err) {
      notify({ status: 'error', title: 'The change was not made', description: err instanceof Error ? err.message : undefined });
    } finally {
      setConfirming(false);
    }
  };

  const add = async (next: string) => {
    setAdding(true);
    try {
      await addRelease(model.modelId, next as WindowsRelease);
      notify({ status: 'success', title: `${releaseLabel(next as WindowsRelease)} added to ${model.displayName}.` });
      setSearchParams({ release: next }, { replace: true });
    } catch (err) {
      notify({ status: 'error', title: 'Could not add the Windows release', description: err instanceof Error ? err.message : undefined });
    } finally {
      setAdding(false);
    }
  };

  const openAddCustom = async () => {
    try {
      setCustomModels(await fetchModels());
    } catch (err) {
      notify({ status: 'error', title: 'Could not load the models', description: err instanceof Error ? err.message : undefined });
    }
  };

  const dismiss = async (customId: string) => {
    try {
      await dismissCustomNotice(customId, model.modelId);
      await load();
    } catch (err) {
      notify({ status: 'error', title: 'Could not dismiss the notice', description: err instanceof Error ? err.message : undefined });
    }
  };

  const previousSet = page.previousSetVersion;
  const copy: ConfirmImpactCopy | null = !confirm ? null : confirm.kind === 'rollback' ? {
    confirmTitle: `Roll back to driver set ${previousSet}?`,
    impact: `Devices imaged from now on get driver set ${previousSet}. Running deployments keep the set they started with. The next sync does not reinstate set ${page.setVersion}.`,
    confirmLabel: 'Roll back',
    destructive: false,
  } : confirm.kind === 'removeModel' ? {
    confirmTitle: `Remove ${model.displayName}?`,
    impact: 'Devices of this model imaged from now on get no drivers from Cloud Imaging. Running deployments keep their driver set.',
    confirmLabel: 'Remove',
    destructive: true,
  } : confirm.kind === 'removeRelease' ? {
    confirmTitle: `Remove ${releaseLabel(page.release)} from ${model.displayName}?`,
    impact: `Devices of this model installing ${releaseLabel(page.release)} from now on get no drivers from Cloud Imaging. Running deployments keep their driver set.`,
    confirmLabel: 'Remove',
    destructive: true,
  } : confirm.kind === 'removeCustom' ? {
    confirmTitle: `Remove ${confirm.name}?`,
    impact: 'Devices imaged from now on no longer get this custom component. Running deployments keep the version they started with.',
    confirmLabel: 'Remove',
    destructive: true,
  } : confirm.kind === 'undoRollBack' ? {
    confirmTitle: `Undo the roll back of ${confirm.component.name}?`,
    impact: `The next driver set uses the version ${vendorLabel(model.vendor)} publishes now instead of ${confirm.component.rollBack?.version ?? confirm.component.version ?? 'the held version'}. Devices get it from their next deployment once the set is built 2 minutes after this change.`,
    confirmLabel: 'Undo roll back',
    destructive: false,
  } : {
    confirmTitle: `Remove ${confirm.component.name}?`,
    impact: `${vendorLabel(model.vendor)} no longer publishes this component. Devices of this model imaged from now on no longer get it, once the next driver set is built 2 minutes after this change.`,
    confirmLabel: 'Remove',
    destructive: true,
  };
  const removal = confirm?.kind === 'removeModel' || confirm?.kind === 'removeRelease';
  const addable = WINDOWS_RELEASES.filter(r => !model.releases.includes(r));
  const rollBackHint = previousSet ? `Devices get driver set ${previousSet} instead of ${page.setVersion} from their next deployment` : 'No previous driver set to roll back to';
  const history = {
    modelId: model.modelId,
    release: page.release,
    vendor: model.vendor,
    setVersion: page.setVersion,
    ...(canManageDrivers ? {
      onRollBack: (component: DriverComponentRow) => setRollingBack(component),
      onUndoRollBack: (component: DriverComponentRow) => setConfirm({ kind: 'undoRollBack', component }),
    } : {}),
  };

  const pending = page.pending;
  // A pending set already has its number; otherwise the next build takes the one after the current set.
  const nextSet = page.pendingVersion ?? (page.setVersion ?? 0) + 1;
  const waitingApproval = page.components.filter(c => c.state === 'WaitingForApproval').length;

  return (
    <div className="space-y-6">
      <div className="space-y-2">
        <nav aria-label="Breadcrumb" className="flex items-center gap-1 text-xs text-muted-foreground">
          <Link to="/drivers?tab=models" className="hover:text-foreground hover:underline">Models</Link>
          <ChevronRight className="h-3 w-3 shrink-0" aria-hidden="true" />
          <span className="truncate">{model.displayName}</span>
        </nav>
        <div className="flex min-w-0 items-center gap-2">
          <h2 className="truncate text-xl font-semibold" title={model.displayName}>{model.displayName}</h2>
          <Badge variant="outline" className="shrink-0">{matchKeyLabel(model.vendor, model.matchKeys)}</Badge>
        </div>
        {canManageDrivers && (
          <div className="flex justify-end gap-2">
            <Tooltip content={blocked ?? 'Checks this model for new component versions.'}>
              <span>
                <Button variant="outline" onClick={() => void check()} disabled={blocked !== null} loading={busy === 'check'}><RefreshCw />Check for updates</Button>
              </span>
            </Tooltip>
            <Button variant="outline" onClick={() => void openAddCustom()}><Plus />Add custom component</Button>
            <Tooltip content={rollBackHint}>
              <span>
                <Button variant="outline" onClick={() => open({ kind: 'rollback' })} disabled={!previousSet}><Undo2 />Roll back driver set</Button>
              </span>
            </Tooltip>
            <Button variant="outline" onClick={() => open({ kind: 'removeModel' })}><Trash2 />Remove model</Button>
          </div>
        )}
      </div>

      {model.state === 'Draft' ? (
        <Banner tone="warning" icon={<CircleDashed className="h-5 w-5" aria-hidden="true" />} title="Onboarding is not complete. Nothing is downloaded and devices of this model get no drivers until you complete it." detail="Choose what each component does below. Downloading starts when you complete onboarding.">
          {canManageDrivers && <Button onClick={() => void complete()} loading={busy === 'complete'}><Check />Complete onboarding</Button>}
        </Banner>
      ) : (
        <>
          {waitingApproval > 0 && (
            <Banner
              tone="warning"
              icon={<CircleAlert className="h-5 w-5" aria-hidden="true" />}
              title={`${waitingApproval === 1 ? '1 new component is' : `${waitingApproval} new components are`} waiting for approval. They are not installed until you approve them.`}
              detail="Approve a component in the Source column. It keeps the action shown, which you can change first."
            />
          )}
          {pending && (
            <Banner
              tone="info"
              icon={<History className="h-5 w-5" aria-hidden="true" />}
              title={`${pending.changes === 1 ? '1 change is' : `${pending.changes} changes are`} waiting. Driver set ${nextSet} is built at ${formatTimestamp(pending.buildAt).slice(11)}, 2 minutes after the last change.`}
              detail={page.setVersion
                ? `Devices keep getting driver set ${page.setVersion} until set ${nextSet} is built and its components are downloaded.`
                : `Devices of this model get no drivers until set ${nextSet} is built and its components are downloaded.`}
            >
              {canManageDrivers && <Button variant="outline" onClick={() => void build()} loading={busy === 'build'}><Hammer />Build now</Button>}
            </Banner>
          )}
          {page.preparing && (
            <Banner
              icon={<Download className="h-5 w-5" aria-hidden="true" />}
              title={`Preparing driver set ${page.preparing.setVersion} for ${releaseLabel(page.release)}: ${page.preparing.done} of ${page.preparing.total} components downloaded.`}
              detail={page.setVersion ? `Devices keep getting driver set ${page.setVersion} until it is ready.` : 'Devices of this model get no drivers until it is ready.'}
            />
          )}
          {(page.notices ?? []).map(notice => (
            <Banner
              key={notice.customId}
              tone="info"
              icon={<ArrowRight className="h-5 w-5" aria-hidden="true" />}
              title={`${vendorLabel(model.vendor)} has published a new ${notice.vendorComponentName} since the custom component was added.`}
              detail={`${notice.componentId}, version ${notice.version ?? 'unknown'}${notice.releasedAt ? `, released ${notice.releasedAt}` : ''}. Test it, then remove the custom component to use ${vendorLabel(model.vendor)}'s driver.`}
            >
              {canManageDrivers && (
                <>
                  <Button variant="outline" onClick={() => open({ kind: 'removeCustom', customId: notice.customId, name: notice.name })}><Trash2 />Remove custom component</Button>
                  <Tooltip content="Dismiss">
                    <Button variant="ghost" size="icon" aria-label="Dismiss" onClick={() => void dismiss(notice.customId)}><X /></Button>
                  </Tooltip>
                </>
              )}
            </Banner>
          ))}
        </>
      )}

      <div className="grid grid-cols-5 gap-4">
        <Tile label={actionLabel('Offline')} value={String(tiles.offline)} />
        <Tile label={actionLabel('OfflineAndSpecialize')} value={String(tiles.offlineAndSpecialize)} />
        <Tile label={actionLabel('Specialize')} value={String(tiles.specialize)} />
        <Tile label={actionLabel('Skip')} value={String(tiles.skip)} />
        <Tile label={<SizeHead />} value={formatSize(tiles.downloadPerDeviceBytes)} />
      </div>

      <div className="flex flex-wrap items-center gap-2">
        <SearchInput value={search} onValueChange={setSearch} placeholder="Search components" aria-label="Search components" wrapperClassName="w-[260px]" />
        <Select
          value={page.release}
          onValueChange={value => setSearchParams({ release: value }, { replace: true })}
          options={model.releases.map(r => ({ value: r, label: releaseLabel(r) }))}
          aria-label="Windows release"
          wrapperClassName="w-[230px]"
        />
        {canManageDrivers && (
          <>
            {addable.length > 0 && (
              <Select
                value=""
                onValueChange={value => void add(value)}
                options={addable.map(r => ({ value: r, label: releaseLabel(r) }))}
                placeholder="Add Windows release"
                aria-label="Add Windows release"
                disabled={adding}
                wrapperClassName="w-[230px]"
              />
            )}
            <Tooltip content={model.releases.length > 1 ? `Stops installing drivers for ${releaseLabel(page.release)} on this model.` : 'Remove the model instead.'}>
              <span>
                <Button variant="outline" onClick={() => open({ kind: 'removeRelease' })} disabled={model.releases.length < 2}><X />Remove release</Button>
              </span>
            </Tooltip>
          </>
        )}
        <Select value={action} onValueChange={setAction} options={[{ value: ALL, label: 'All actions' }, ...DRIVER_ACTIONS.map((a: DriverAction) => ({ value: a, label: actionLabel(a) }))]} aria-label="Action" wrapperClassName="w-[170px]" />
        <Select value={category} onValueChange={setCategory} options={[{ value: ALL, label: 'All categories' }, ...categories.map(c => ({ value: c, label: c }))]} aria-label="Category" wrapperClassName="w-[190px]" />
        <Button variant={updatedOnly ? 'secondary' : 'outline'} aria-pressed={updatedOnly} onClick={() => setUpdatedOnly(on => !on)}>
          {`${updatedCount} updated by today's sync`}
        </Button>
      </div>

      <ComponentTable components={components} total={page.components.length} editing={editing} history={history} />

      {customModels && (
        <AddCustomComponentDialog
          models={customModels}
          initialModelId={model.modelId}
          onClose={() => setCustomModels(null)}
          onAdded={custom => {
            setCustomModels(null);
            notify({ status: 'success', title: `${custom.name} added.` });
            void load();
          }}
        />
      )}

      {rollingBack && (
        <RollBackComponentDialog
          modelId={model.modelId}
          release={page.release}
          vendor={model.vendor}
          component={rollingBack}
          onCancel={() => setRollingBack(null)}
          onDone={row => {
            setRollingBack(null);
            notify({ status: 'success', title: `${row.name} rolled back to ${row.version ?? ''}.` });
            void load();
          }}
        />
      )}

      {confirm && copy && (
        <ConfirmImpactDialog
          copy={copy}
          busy={confirming}
          onCancel={() => setConfirm(null)}
          onConfirm={() => void run()}
          titleId="driver-model-confirm-title"
          icon={confirm.kind === 'rollback' || confirm.kind === 'undoRollBack' ? Undo2 : Trash2}
          details={removal ? <RemovalDetails impact={impact} /> : confirm.kind === 'removeCustom' ? <CustomRemovalDetails targets={customTargets} vendor={vendorLabel(model.vendor)} failed={customTargetsFailed} onRetry={() => loadCustomTargets(confirm.customId)} /> : undefined}
          confirmDisabled={(removal && impact === null) || (confirm.kind === 'removeCustom' && (customTargets === null || customTargetsFailed))}
        />
      )}
    </div>
  );
}
