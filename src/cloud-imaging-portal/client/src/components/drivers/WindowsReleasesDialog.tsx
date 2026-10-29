import React, { useEffect, useId, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { CircleCheck, Plus, Trash2, X } from 'lucide-react';
import { Button } from '../ui/button.tsx';
import { Checkbox } from '../ui/checkbox.tsx';
import { Skeleton } from '../ui/skeleton.tsx';
import { cn } from '../../lib/utils.ts';
import {
  WINDOWS_RELEASES, addRelease, fetchCatalogModels, fetchModelPage, fetchRemovalImpact, formatSize, previewModels, releaseLabel, removeRelease, vendorLabel,
  type DriverModelRow, type DriverRemovalImpact, type WindowsRelease,
} from '../../lib/drivers.ts';
import { useDialogFocus } from './useDialogFocus.ts';

/** Components and download size of one release: what the model has, or what adding it would bring. */
type ReleaseSummary = { components: number; included: number; sizeBytes: number } | 'failed';

function plural(count: number, one: string, many: string): string {
  return `${count} ${count === 1 ? one : many}`;
}

/**
 * Which Windows releases a model is onboarded for. Only releases the vendor publishes drivers for
 * this model are listed; nothing changes until Save, which adds the ticked releases first so the
 * model never ends up without one.
 */
export function WindowsReleasesDialog({ model, onClose, onSaved }: {
  model: DriverModelRow;
  onClose: () => void;
  onSaved: (releases: WindowsRelease[]) => void;
}): React.ReactElement {
  const dialogRef = useRef<HTMLDivElement>(null);
  useDialogFocus(dialogRef, onClose);
  const titleId = useId();
  const vendor = vendorLabel(model.vendor);
  const [releases, setReleases] = useState<WindowsRelease[] | null>(null);
  const [summaries, setSummaries] = useState<Partial<Record<WindowsRelease, ReleaseSummary>>>({});
  const [impacts, setImpacts] = useState<Partial<Record<WindowsRelease, DriverRemovalImpact>>>({});
  const [chosen, setChosen] = useState<WindowsRelease[]>(model.releases);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    let cancelled = false;
    const summarise = (release: WindowsRelease, summary: ReleaseSummary) => { if (!cancelled) setSummaries(current => ({ ...current, [release]: summary })); };
    fetchCatalogModels(model.vendor)
      .then(list => {
        if (cancelled) return;
        const entry = list.models.find(m => m.onboardedModelId === model.modelId);
        const offered = WINDOWS_RELEASES.filter(r => model.releases.includes(r) || entry?.releases.includes(r));
        setReleases(offered);
        for (const release of offered) {
          if (model.releases.includes(release)) {
            fetchModelPage(model.modelId, release)
              .then(page => {
                const { offline, offlineAndSpecialize, specialize, skip, downloadPerDeviceBytes } = page.tiles;
                summarise(release, { components: offline + offlineAndSpecialize + specialize + skip, included: offline + offlineAndSpecialize + specialize, sizeBytes: downloadPerDeviceBytes });
              })
              .catch(() => summarise(release, 'failed'));
          } else if (entry) {
            previewModels(model.vendor, [entry.catalogModelKey], [release])
              .then(result => {
                const row = result.models[0];
                if (!row) { summarise(release, 'failed'); return; }
                const included = row.offline + row.offlineAndSpecialize + row.specialize;
                summarise(release, { components: included + row.skip, included, sizeBytes: row.sizeBytes });
              })
              .catch(() => summarise(release, 'failed'));
          }
        }
      })
      .catch((err: unknown) => { if (!cancelled) setLoadError(err instanceof Error ? err.message : `Could not load the Windows releases ${vendor} publishes for this model.`); });
    return () => { cancelled = true; };
  }, [model.modelId, model.vendor, model.releases, vendor]);

  const toggle = (release: WindowsRelease, on: boolean) => {
    setChosen(current => (on ? [...current, release] : current.filter(r => r !== release)));
    if (!on && model.releases.includes(release) && !impacts[release]) {
      fetchRemovalImpact(model.modelId, release).then(impact => setImpacts(current => ({ ...current, [release]: impact }))).catch(() => undefined);
    }
  };

  const adds = (releases ?? []).filter(r => chosen.includes(r) && !model.releases.includes(r));
  const removes = model.releases.filter(r => !chosen.includes(r));
  const changed = adds.length + removes.length > 0;

  const save = async () => {
    setSaving(true);
    setSaveError(null);
    try {
      for (const release of adds) await addRelease(model.modelId, release);
      for (const release of removes) await removeRelease(model.modelId, release);
      onSaved(chosen.filter(r => !removes.includes(r)));
    } catch (err) {
      setSaveError(err instanceof Error ? err.message : 'The Windows releases could not be saved.');
      setSaving(false);
    }
  };

  const detail = (release: WindowsRelease, on: boolean, onboarded: boolean, last: boolean): string => {
    if (last) return 'The last release stays. Use Remove to remove the model.';
    if (onboarded && !on) {
      const impact = impacts[release];
      return impact ? `Deletes ${plural(impact.sets, 'driver set', 'driver sets')} and ${plural(impact.overrides, 'override', 'overrides')}. Devices installing it get no drivers.` : 'Devices installing it get no drivers.';
    }
    const summary = summaries[release];
    if (summary === undefined) return '';
    if (summary === 'failed') return `Could not read ${vendor}'s catalog for this release.`;
    return `${plural(summary.components, 'component', 'components')}, ${summary.included} included${onboarded ? '' : ' after your rules'}`;
  };

  return createPortal(
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 p-4">
      <div ref={dialogRef} role="dialog" aria-modal="true" aria-labelledby={titleId} className="flex max-h-full w-full max-w-xl flex-col rounded-lg border border-border bg-background shadow-xl">
        <div className="flex items-start justify-between gap-3 border-b border-border p-5">
          <div className="min-w-0 space-y-1">
            <h2 id={titleId} className="text-base font-semibold">Windows releases</h2>
            <p className="text-sm text-muted-foreground">{`Choose the Windows releases you deploy to ${model.displayName}.`}</p>
          </div>
          <Button variant="ghost" size="icon" aria-label="Close" onClick={onClose} disabled={saving}><X /></Button>
        </div>

        <div className="min-h-0 flex-1 space-y-3 overflow-y-auto p-5">
          {loadError ? (
            <p role="alert" className="text-sm text-destructive">{loadError}</p>
          ) : releases === null ? (
            <><Skeleton className="h-14 w-full" /><Skeleton className="h-14 w-full" /></>
          ) : (
            <ul className="divide-y divide-border/70 overflow-hidden rounded-md border border-border" aria-label="Windows releases">
              {releases.map(release => {
                const on = chosen.includes(release);
                const onboarded = model.releases.includes(release);
                const last = on && chosen.length === 1;
                const summary = summaries[release];
                const nameId = `${titleId}-${release}`;
                const status = onboarded && on ? 'onboarded' : !onboarded && on ? 'adds' : onboarded ? 'removes' : null;
                return (
                  <li key={release}>
                    <label className={cn('flex items-center gap-3 px-3 py-2.5', last || saving ? 'cursor-not-allowed' : 'cursor-pointer', status === 'adds' && 'bg-primary/5', status === 'removes' && 'bg-destructive/10')}>
                      <Checkbox checked={on} disabled={last || saving} onCheckedChange={next => toggle(release, next)} labelledBy={nameId} />
                      <span className="min-w-0 flex-1">
                        <span id={nameId} className="block text-sm font-medium">{releaseLabel(release)}</span>
                        {summary === undefined && !(onboarded && !on) && !last
                          ? <Skeleton className="mt-1 h-3 w-40" />
                          : <span className="block text-xs text-muted-foreground">{detail(release, on, onboarded, last)}</span>}
                      </span>
                      <span className="w-20 shrink-0 text-right text-sm tabular-nums text-muted-foreground">{summary && summary !== 'failed' ? formatSize(summary.sizeBytes) : ''}</span>
                      <span className="flex w-28 shrink-0 items-center gap-2 text-sm">
                        {status === 'onboarded' && <><CircleCheck className="h-4 w-4 shrink-0 text-emerald-600 dark:text-emerald-400" aria-hidden="true" />Onboarded</>}
                        {status === 'adds' && <><Plus className="h-4 w-4 shrink-0 text-blue-600 dark:text-blue-400" aria-hidden="true" />Adds</>}
                        {status === 'removes' && <><Trash2 className="h-4 w-4 shrink-0 text-destructive" aria-hidden="true" />Removes</>}
                      </span>
                    </label>
                  </li>
                );
              })}
            </ul>
          )}
          {saveError && <p role="alert" className="text-sm text-destructive">{saveError}</p>}
        </div>

        <div className="flex items-center justify-between gap-3 border-t border-border px-5 py-3">
          <span className="text-xs text-muted-foreground">Running deployments keep the driver set they started with.</span>
          <div className="flex gap-2">
            <Button variant="outline" onClick={onClose} disabled={saving}>Cancel</Button>
            <Button onClick={() => void save()} disabled={!changed} loading={saving}>Save</Button>
          </div>
        </div>
      </div>
    </div>,
    document.body,
  );
}
