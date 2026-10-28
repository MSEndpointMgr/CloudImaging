import React, { useEffect, useId, useState } from 'react';
import { Undo2 } from 'lucide-react';
import { ConfirmImpactDialog } from '../ConfirmImpactDialog.tsx';
import { Select } from '../ui/select.tsx';
import {
  fetchComponentVersions, formatSize, rollBackComponent, vendorLabel,
  type DriverComponentRow, type DriverComponentVersion, type DriverRollBackUntil, type DriverVendor, type WindowsRelease,
} from '../../lib/drivers.ts';

/**
 * Roll back component: choose an earlier version (checked at the vendor first) and how long it is
 * held. A version that is no longer stored is downloaded again and verified before use; when that
 * fails the reason is shown and the dialog stays open.
 */
export function RollBackComponentDialog({ modelId, release, vendor, component, onCancel, onDone }: {
  modelId: string;
  release: WindowsRelease;
  vendor: DriverVendor;
  component: DriverComponentRow;
  onCancel: () => void;
  onDone: (row: DriverComponentRow) => void;
}): React.ReactElement {
  const vendorName = vendorLabel(vendor);
  const untilName = useId();
  const [versions, setVersions] = useState<DriverComponentVersion[] | null>(null);
  const [version, setVersion] = useState('');
  const [until, setUntil] = useState<DriverRollBackUntil>('NewerVersion');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let current = true;
    fetchComponentVersions(modelId, release, component.lineageKey, true)
      .then(list => {
        if (!current) return;
        // Earlier versions only: never the one devices get now.
        const earlier = list.filter((v, i) => i > 0 && v.version !== component.version);
        setVersions(earlier);
        setVersion(earlier.find(v => v.status !== 'Unavailable')?.version ?? '');
      })
      .catch((err: unknown) => { if (current) { setVersions([]); setError(err instanceof Error ? err.message : 'Could not load the version history.'); } });
    return () => { current = false; };
  }, [modelId, release, component.lineageKey, component.version]);

  const chosen = versions?.find(v => v.version === version);
  const confirm = async () => {
    setBusy(true);
    setError(null);
    try {
      onDone(await rollBackComponent(modelId, release, component.lineageKey, version, until));
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not roll back the component.');
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
            label: `${v.version} (driver sets ${v.firstSet}${v.lastSet && v.lastSet !== v.firstSet ? ` to ${v.lastSet}` : ''})`,
            description: v.status === 'Stored' ? 'Stored' : v.status === 'DownloadsOnRollBack' ? `Downloads ${formatSize(v.sizeBytes)}` : `No longer available from ${vendorName}`,
            disabled: v.status === 'Unavailable',
          }))}
          placeholder={versions === null ? `Checking ${vendorName}` : 'No earlier version'}
          disabled={versions === null || busy}
          aria-label="Version"
        />
      </div>
      <fieldset className="space-y-2">
        <legend className="text-sm font-medium">Until</legend>
        <label className="flex items-start gap-2 text-sm">
          <input type="radio" name={untilName} className="mt-1" checked={until === 'NewerVersion'} onChange={() => setUntil('NewerVersion')} disabled={busy} />
          <span>
            <span className="block">{`${vendorName} publishes a version newer than ${component.rollBack?.skippedVersion ?? component.version ?? 'the current one'}`}</span>
            <span className="block text-sm text-muted-foreground">The component then moves forward to that version on its own.</span>
          </span>
        </label>
        <label className="flex items-start gap-2 text-sm">
          <input type="radio" name={untilName} className="mt-1" checked={until === 'Undone'} onChange={() => setUntil('Undone')} disabled={busy} />
          <span>I undo the roll back</span>
        </label>
      </fieldset>
      {chosen && (
        <p className="text-sm text-muted-foreground">
          {chosen.status === 'Stored'
            ? 'This version is stored, so the next driver set uses it at once.'
            : `This version is no longer stored, so it is downloaded again from ${vendorName} (${formatSize(chosen.sizeBytes)}) and checked against ${vendorName}'s checksum before the next driver set uses it.`}
        </p>
      )}
      {error && <p role="alert" className="text-sm text-destructive">{error}</p>}
    </div>
  );

  return (
    <ConfirmImpactDialog
      copy={{
        confirmTitle: `Roll back ${component.name}?`,
        impact: 'Devices of this model imaged from now on get the version you choose. Other components keep updating. Running deployments keep the version they started with.',
        confirmLabel: 'Roll back',
        destructive: false,
      }}
      icon={Undo2}
      busy={busy}
      onCancel={onCancel}
      onConfirm={() => void confirm()}
      titleId="rollback-component-title"
      details={details}
      confirmDisabled={!chosen}
    />
  );
}
