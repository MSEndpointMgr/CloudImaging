import React from 'react';
import { Label } from './ui/label.tsx';
import { Select } from './ui/select.tsx';
import { releaseLabel, sortReleases, useWindowsReleases, type WindowsRelease } from '../lib/drivers.ts';

const NONE = 'none';

/** The OS image's Windows release, which decides the driver set devices get. Empty means no drivers. */
export function WindowsReleaseField({ id, value, onChange, disabled }: { id: string; value: WindowsRelease | null; onChange: (release: WindowsRelease | null) => void; disabled?: boolean }): React.ReactElement {
  const { list, error } = useWindowsReleases();
  // The image's own release stays choosable even when the list cannot be read.
  const releases = sortReleases([...(list?.releases ?? []), ...(value ? [value] : [])]);
  return (
    <div className="space-y-2">
      <Label htmlFor={id}>Windows release</Label>
      <Select
        id={id}
        value={value ?? NONE}
        onValueChange={next => onChange(next === NONE ? null : next)}
        options={[{ value: NONE, label: 'Not set' }, ...releases.map(r => ({ value: r, label: releaseLabel(r) }))]}
        disabled={disabled}
        aria-label="Windows release"
        wrapperClassName="w-[230px]"
      />
      <p className="text-sm text-muted-foreground">Used to pick the driver set for each device model. Without it no drivers are installed.</p>
      {error && <p className="text-sm text-destructive" role="alert">{error}</p>}
      {list?.problem && <p className="text-sm text-muted-foreground">Newer releases may be missing. {list.problem}</p>}
    </div>
  );
}
