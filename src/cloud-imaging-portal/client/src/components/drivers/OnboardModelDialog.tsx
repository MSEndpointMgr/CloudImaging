import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { useNavigate } from 'react-router-dom';
import { ChevronRight, CircleAlert, CircleX, Info, Loader, Plus, RefreshCw, X } from 'lucide-react';
import { Button } from '../ui/button.tsx';
import { Checkbox, CheckboxField } from '../ui/checkbox.tsx';
import { EmptyState } from '../ui/empty-state.tsx';
import { SearchInput } from '../ui/search-input.tsx';
import { Select } from '../ui/select.tsx';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '../ui/table.tsx';
import { cn, formatTimestamp } from '../../lib/utils.ts';
import {
  WINDOWS_RELEASES, fetchCatalogModels, formatSize, onboardModels, previewModels, releaseLabel, vendorLabel,
  type DriverCatalogModelList, type DriverPreviewResult, type DriverVendor, type DriverVendorRow, type WindowsRelease,
} from '../../lib/drivers.ts';
import { SizeHead } from './ModelsTable.tsx';

type ListState = { kind: 'fetching' } | { kind: 'ready'; list: DriverCatalogModelList } | { kind: 'failed'; detail: string };

interface OnboardModelDialogProps {
  vendors: DriverVendorRow[];
  onClose: () => void;
}

/**
 * Onboard model, two steps: choose models from the vendor's model list, then the Windows
 * releases with a preview per model. Onboard creates drafts (nothing is downloaded) and opens the
 * first one's page.
 */
export function OnboardModelDialog({ vendors, onClose }: OnboardModelDialogProps): React.ReactElement {
  const navigate = useNavigate();
  const supported = vendors.filter(v => v.supported);
  const [vendor, setVendor] = useState<DriverVendor>(supported[0]?.vendor ?? 'Hp');
  const [listState, setListState] = useState<ListState>({ kind: 'fetching' });
  const [search, setSearch] = useState('');
  const [selected, setSelected] = useState<string[]>([]);
  const [step, setStep] = useState<1 | 2>(1);
  const [releases, setReleases] = useState<WindowsRelease[]>([]);
  const [preview, setPreview] = useState<DriverPreviewResult | null>(null);
  const [previewing, setPreviewing] = useState(false);
  const [onboarding, setOnboarding] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const dialogRef = useRef<HTMLDivElement>(null);
  const request = useRef(0);

  // Switching vendor while a list is loading never shows the earlier vendor's models.
  const load = useCallback(async (target: DriverVendor) => {
    const id = ++request.current;
    setListState({ kind: 'fetching' });
    try {
      const list = await fetchCatalogModels(target);
      if (id === request.current) setListState({ kind: 'ready', list });
    } catch (err) {
      if (id === request.current) setListState({ kind: 'failed', detail: err instanceof Error ? err.message : 'The model list could not be read.' });
    }
  }, []);

  useEffect(() => { void load(vendor); }, [load, vendor]);

  // Keyboard: Escape closes, focus starts in the dialog and returns to where it was on close.
  useEffect(() => {
    const previous = document.activeElement as HTMLElement | null;
    dialogRef.current?.querySelector<HTMLElement>('button, input, [tabindex="0"]')?.focus();
    const onKey = (event: KeyboardEvent) => { if (event.key === 'Escape') onClose(); };
    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('keydown', onKey);
      previous?.focus();
    };
  }, [onClose]);

  const models = useMemo(() => (listState.kind === 'ready' ? listState.list.models : []), [listState]);
  const visible = useMemo(() => {
    const term = search.trim().toLowerCase();
    return term ? models.filter(m => m.displayName.toLowerCase().includes(term) || m.matchKeys.some(k => k.toLowerCase().includes(term))) : models;
  }, [models, search]);
  const chosen = models.filter(m => selected.includes(m.catalogModelKey));
  const shared = WINDOWS_RELEASES.filter(r => chosen.length > 0 && chosen.every(m => m.releases.includes(r)));
  const vendorRow = vendors.find(v => v.vendor === vendor);
  const name = vendorLabel(vendor);

  const toggle = (key: string, on: boolean) => setSelected(current => (on ? [...current, key] : current.filter(k => k !== key)));

  const goToReleases = () => {
    setReleases(shared.slice(0, 1));
    setStep(2);
  };

  useEffect(() => {
    if (step !== 2 || releases.length === 0) {
      setPreview(null);
      return;
    }
    let cancelled = false;
    setPreviewing(true);
    setError(null);
    previewModels(vendor, selected, releases)
      .then(result => { if (!cancelled) setPreview(result); })
      .catch((err: unknown) => { if (!cancelled) setError(err instanceof Error ? err.message : 'Could not preview the models.'); })
      .finally(() => { if (!cancelled) setPreviewing(false); });
    return () => { cancelled = true; };
  }, [step, releases, selected, vendor]);

  const onboard = async () => {
    setOnboarding(true);
    setError(null);
    try {
      const { modelIds } = await onboardModels(vendor, selected, releases);
      onClose();
      if (modelIds[0]) void navigate(`/drivers/models/${modelIds[0]}`);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not onboard the models.');
      setOnboarding(false);
    }
  };

  return createPortal(
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 p-4">
      <div ref={dialogRef} role="dialog" aria-modal="true" aria-labelledby="onboard-model-title" className="flex max-h-[90vh] w-full max-w-3xl flex-col rounded-lg border border-border bg-background shadow-xl">
        <div className="flex items-start justify-between gap-3 border-b border-border p-5">
          <div className="min-w-0 space-y-1">
            <h2 id="onboard-model-title" className="text-base font-semibold">
              {step === 1 ? 'Onboard models' : `Onboard ${selected.length} ${selected.length === 1 ? 'model' : 'models'}`}
            </h2>
            <p className="text-sm text-muted-foreground">
              {step === 1
                ? `Models come from ${name}'s driver catalog. Devices are matched on their ${vendorRow?.matchKeyType ?? 'model key'}.`
                : `Choose the Windows releases you deploy to ${selected.length === 1 ? 'this model' : 'these models'}.`}
            </p>
          </div>
          <Button variant="ghost" size="icon" aria-label="Close" onClick={onClose} disabled={onboarding}><X /></Button>
        </div>

        <div className="min-h-0 flex-1 space-y-5 overflow-y-auto p-5">
          {step === 1 ? (
            <>
              <div className="flex items-center gap-2">
                <Select
                  value={vendor}
                  onValueChange={value => { setVendor(value as DriverVendor); setSelected([]); setSearch(''); }}
                  options={supported.map(v => ({ value: v.vendor, label: vendorLabel(v.vendor) }))}
                  aria-label="Vendor"
                  wrapperClassName="w-40"
                />
                <SearchInput value={search} onValueChange={setSearch} placeholder="Search models" aria-label="Search models" wrapperClassName="flex-1" disabled={listState.kind !== 'ready'} />
              </div>

              {listState.kind === 'ready' && listState.list.stale && listState.list.warning && (
                <p className="flex items-center gap-2 text-sm text-amber-700 dark:text-amber-400">
                  <CircleAlert className="h-4 w-4 shrink-0" aria-hidden="true" />
                  {listState.list.warning}
                </p>
              )}

              {listState.kind === 'fetching' && (
                <div className="rounded-md border border-border">
                  <EmptyState icon={Loader} title={`Getting ${name}'s model list`} description="This only happens the first time, or when the list is more than 7 days old." />
                </div>
              )}
              {listState.kind === 'failed' && (
                <div className="rounded-md border border-border">
                  <EmptyState
                    icon={CircleX}
                    iconClassName="text-destructive"
                    title={`Couldn't get ${name}'s model list`}
                    description={listState.detail}
                    action={<Button variant="outline" onClick={() => void load(vendor)}><RefreshCw />Try again</Button>}
                  />
                </div>
              )}
              {listState.kind === 'ready' && (
                <div className="overflow-hidden rounded-md border border-border">
                  <ul className="max-h-80 divide-y divide-border/70 overflow-y-auto" aria-label={`${name} models`}>
                    {visible.map(model => {
                      const onboarded = model.onboardedModelId !== null;
                      const on = onboarded || selected.includes(model.catalogModelKey);
                      const nameId = `onboard-${model.catalogModelKey}`;
                      return (
                        <li key={model.catalogModelKey}>
                          <label className={cn('flex items-center gap-3 px-3 py-2', onboarded ? 'cursor-not-allowed' : 'cursor-pointer', on && !onboarded && 'bg-primary/5')}>
                            <Checkbox checked={on} disabled={onboarded} onCheckedChange={next => toggle(model.catalogModelKey, next)} labelledBy={nameId} />
                            <span className="min-w-0 flex-1">
                              <span id={nameId} className={cn('block truncate text-sm font-medium', onboarded && 'text-muted-foreground')} title={model.displayName}>{model.displayName}</span>
                              <span className="block truncate text-xs text-muted-foreground">
                                <span className="font-mono">{model.matchKeys.join(', ')}</span>
                                {onboarded && ', already onboarded'}
                              </span>
                            </span>
                          </label>
                        </li>
                      );
                    })}
                  </ul>
                  <div className="flex items-center justify-between gap-4 border-t border-border bg-muted/30 px-3 py-2 text-xs text-muted-foreground">
                    <span>{`${selected.length} selected`}</span>
                    <span className="truncate">
                      {`${visible.length.toLocaleString('en-US')} of ${models.length.toLocaleString('en-US')} ${name} models`}
                      {listState.list.fetchedAt ? `, list from ${formatTimestamp(listState.list.fetchedAt)}` : ''}
                    </span>
                  </div>
                </div>
              )}

              {vendorRow && !vendorRow.syncEnabled && (
                <p className="flex items-center gap-2 text-sm text-muted-foreground">
                  <Info className="h-4 w-4 shrink-0" aria-hidden="true" />
                  {`Sync is off for ${name}. Completing onboarding turns it on, so these models get updates.`}
                </p>
              )}
            </>
          ) : (
            <>
              <div role="group" aria-label="Windows releases" className="grid grid-cols-3 gap-2">
                {shared.map(release => (
                  <CheckboxField
                    key={release}
                    checked={releases.includes(release)}
                    onCheckedChange={on => setReleases(current => (on ? [...current, release] : current.filter(r => r !== release)))}
                    label={releaseLabel(release)}
                    className="gap-2 rounded-md border border-border px-3 py-2"
                  />
                ))}
              </div>

              <div>
              <p className="text-sm font-medium">
                {releases.length > 0
                  ? `Components per model for ${releases.map(releaseLabel).join(' and ')}, after your rules`
                  : 'Components per model, after your rules'}
              </p>

              <div className="mt-2 overflow-hidden rounded-md border border-border">
              <Table className="table-fixed">
                <TableHeader>
                  <TableRow>
                    <TableHead className="w-[28%]">Model</TableHead>
                    <TableHead className="w-[10%] text-right">Offline</TableHead>
                    <TableHead className="w-[24%] text-right">Offline + Specialize</TableHead>
                    <TableHead className="w-[15%] text-right">Specialize</TableHead>
                    <TableHead className="w-[10%] text-right">Skip</TableHead>
                    <TableHead className="w-[13%] text-right"><SizeHead /></TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {chosen.map(model => {
                    const row = preview?.models.find(p => p.catalogModelKey === model.catalogModelKey);
                    const failure = preview?.errors.find(e => e.catalogModelKey === model.catalogModelKey);
                    return (
                      <TableRow key={model.catalogModelKey}>
                        <TableCell className="truncate font-medium" title={model.displayName}>{model.displayName}</TableCell>
                        {row ? (
                          <>
                            <TableCell className="text-right tabular-nums">{row.offline}</TableCell>
                            <TableCell className="text-right tabular-nums">{row.offlineAndSpecialize}</TableCell>
                            <TableCell className="text-right tabular-nums">{row.specialize}</TableCell>
                            <TableCell className="text-right tabular-nums">{row.skip}</TableCell>
                            <TableCell className="text-right tabular-nums">{formatSize(row.sizeBytes)}</TableCell>
                          </>
                        ) : (
                          <TableCell colSpan={5} className="truncate text-muted-foreground">
                            {failure ? failure.detail : previewing ? `Reading ${name}'s catalog for this model` : releases.length === 0 ? 'Choose a Windows release.' : ''}
                          </TableCell>
                        )}
                      </TableRow>
                    );
                  })}
                </TableBody>
              </Table>
              </div>

              <p className="mt-3 text-sm text-muted-foreground">
                Nothing is downloaded yet. Next, choose what each component does on the model&apos;s page, then complete onboarding.
              </p>
              </div>
            </>
          )}
          {error && <p role="alert" className="text-sm text-destructive">{error}</p>}
        </div>

        <div className="flex items-center justify-between gap-2 border-t border-border px-5 py-3">
          <span className="text-xs text-muted-foreground">{`Step ${step} of 2`}</span>
          <div className="flex shrink-0 gap-2">
            {step === 1 ? (
              <>
                <Button variant="secondary" onClick={onClose}>Cancel</Button>
                <Button onClick={goToReleases} disabled={listState.kind !== 'ready' || selected.length === 0 || shared.length === 0}>Next<ChevronRight /></Button>
              </>
            ) : (
              <>
                <Button variant="secondary" onClick={() => setStep(1)} disabled={onboarding}>Back</Button>
                <Button onClick={() => void onboard()} loading={onboarding} disabled={releases.length === 0 || previewing}>
                  <Plus />{`Onboard ${selected.length} ${selected.length === 1 ? 'model' : 'models'}`}
                </Button>
              </>
            )}
          </div>
        </div>
      </div>
    </div>,
    document.body,
  );
}
