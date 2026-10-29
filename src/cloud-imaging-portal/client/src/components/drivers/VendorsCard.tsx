import React, { useState } from 'react';
import { Switch } from '../ui/switch.tsx';
import { setVendorSync, vendorLabel, type DriverVendorRow } from '../../lib/drivers.ts';
import { DriverCard, DriverCardRow, DriverCardRows } from './DriverCard.tsx';

/** One row per vendor with its Sync switch; unsupported vendors are listed with the switch off and disabled. */
export function VendorsCard({ vendors, readOnly, onChanged, onError }: {
  vendors: DriverVendorRow[];
  readOnly: boolean;
  onChanged: (row: DriverVendorRow) => void;
  onError: (message: string) => void;
}): React.ReactElement {
  const [saving, setSaving] = useState<string | null>(null);

  const toggle = async (row: DriverVendorRow, on: boolean) => {
    setSaving(row.vendor);
    try {
      onChanged(await setVendorSync(row.vendor, on));
    } catch (err) {
      onError(err instanceof Error ? err.message : 'Could not change the vendor.');
    } finally {
      setSaving(null);
    }
  };

  return (
    <DriverCard title="Vendors" description="Only vendors turned on are checked, and only for their onboarded models.">
      <DriverCardRows>
        {vendors.map(row => {
          const name = vendorLabel(row.vendor);
          const detail = !row.supported
            ? 'Not supported yet.'
            : `${row.onboardedModels} onboarded ${row.onboardedModels === 1 ? 'model' : 'models'}, matched on ${row.matchKeyType ?? 'model'}.`;
          return (
            <DriverCardRow key={row.vendor} label={name} help={detail} mutedLabel={!row.supported}>
              <Switch
                checked={row.supported && row.syncEnabled}
                onCheckedChange={on => void toggle(row, on)}
                label={`Sync ${name}`}
                disabled={readOnly || !row.supported || saving === row.vendor}
              />
            </DriverCardRow>
          );
        })}
      </DriverCardRows>
    </DriverCard>
  );
}
