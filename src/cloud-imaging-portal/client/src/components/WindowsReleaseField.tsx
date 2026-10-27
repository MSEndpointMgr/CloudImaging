import React from 'react';
import { Label } from './ui/label.tsx';
import { Select } from './ui/select.tsx';
import { WINDOWS_RELEASES, releaseLabel, type WindowsRelease } from '../lib/drivers.ts';

const NONE = 'none';

/** The OS image's Windows release, which decides the driver set devices get. Empty means no drivers. */
export function WindowsReleaseField({ id, value, onChange, disabled }: { id: string; value: WindowsRelease | null; onChange: (release: WindowsRelease | null) => void; disabled?: boolean }): React.ReactElement {
  return (
    <div className="space-y-2">
      <Label htmlFor={id}>Windows release</Label>
      <Select
        id={id}
        value={value ?? NONE}
        onValueChange={next => onChange(next === NONE ? null : next as WindowsRelease)}
        options={[{ value: NONE, label: 'Not set' }, ...WINDOWS_RELEASES.map(r => ({ value: r, label: releaseLabel(r) }))]}
        disabled={disabled}
        aria-label="Windows release"
        wrapperClassName="w-[230px]"
      />
      <p className="text-sm text-muted-foreground">Used to pick the driver set for each device model. Without it no drivers are installed.</p>
    </div>
  );
}
