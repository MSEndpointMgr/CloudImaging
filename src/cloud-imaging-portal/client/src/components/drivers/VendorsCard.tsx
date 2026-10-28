import React, { useState } from 'react';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '../ui/card.tsx';
import { Switch } from '../ui/switch.tsx';
import { setVendorSync, vendorLabel, type DriverVendorRow } from '../../lib/drivers.ts';

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
    <Card>
      <CardHeader>
        <CardTitle className="text-base">Vendors</CardTitle>
        <CardDescription>A sync reads the catalogs of vendors that are on. Turning a vendor off keeps its models' current driver sets.</CardDescription>
      </CardHeader>
      <CardContent className="divide-y divide-border">
        {vendors.map(row => {
          const name = vendorLabel(row.vendor);
          const detail = !row.supported
            ? 'Not supported yet.'
            : `${row.onboardedModels} ${row.onboardedModels === 1 ? 'model' : 'models'} onboarded. Matched by ${row.matchKeyType ?? 'model'}.`;
          return (
            <div key={row.vendor} className="flex items-center gap-4 py-3">
              <div className="min-w-0 flex-1">
                <p className="text-sm font-medium">{name}</p>
                <p className="text-sm text-muted-foreground">{detail}</p>
              </div>
              <Switch
                checked={row.supported && row.syncEnabled}
                onCheckedChange={on => void toggle(row, on)}
                label={`Sync ${name}`}
                disabled={readOnly || !row.supported || saving === row.vendor}
              />
            </div>
          );
        })}
      </CardContent>
    </Card>
  );
}
