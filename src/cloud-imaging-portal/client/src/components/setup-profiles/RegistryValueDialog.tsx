import { useState } from 'react';
import { createPortal } from 'react-dom';
import { Check, X } from 'lucide-react';
import { Button } from '../ui/button.tsx';
import { Input } from '../ui/input.tsx';
import { Label } from '../ui/label.tsx';
import { Select } from '../ui/select.tsx';
import {
  HIVE_OPTIONS,
  VALUE_TYPE_OPTIONS,
  validateRegistryValue,
  type SetupRegistryHive,
  type SetupRegistryValue,
  type SetupRegistryValueType,
} from '../../lib/setupProfiles.ts';

const TEXTAREA_CLASS = 'flex min-h-24 w-full rounded-md border border-input bg-transparent px-3 py-2 font-mono text-sm shadow-sm placeholder:text-muted-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background';

/**
 * Add or edit one registry value. Problems use the same rules as Imaging Core and show once the
 * operator tries to apply; nothing is saved until the profile itself is saved.
 */
export function RegistryValueDialog({ value, others, onClose, onApply }: {
  /** The value to edit; omit to add one. */
  value?: SetupRegistryValue;
  /** The profile's other values, to catch two values that set the same thing. */
  others: SetupRegistryValue[];
  onClose: () => void;
  onApply: (value: SetupRegistryValue) => void;
}): React.ReactElement {
  const [draft, setDraft] = useState<SetupRegistryValue>(
    () => value ?? { id: null, name: '', hive: 'Software', key: '', valueName: '', type: 'DWord', data: '' },
  );
  const [attempted, setAttempted] = useState(false);
  const set = (patch: Partial<SetupRegistryValue>) => setDraft((d) => ({ ...d, ...patch }));
  const problems = validateRegistryValue(draft, others);

  const apply = () => {
    setAttempted(true);
    if (problems.length > 0) return;
    onApply({ ...draft, name: draft.name.trim(), key: draft.key.trim() });
  };

  return createPortal(
    <div className="fixed inset-0 z-40 flex items-center justify-center bg-black/60 p-4" role="dialog" aria-modal="true" aria-labelledby="registry-value-title">
      <div className="flex max-h-full w-full max-w-xl flex-col rounded-lg border border-border bg-background shadow-xl">
        <div className="flex items-start justify-between gap-3 border-b border-border p-5">
          <div className="space-y-1">
            <h2 id="registry-value-title" className="text-base font-semibold break-words">{value ? `Edit ${value.name}` : 'Add registry value'}</h2>
            <p className="text-sm text-muted-foreground">Written to the applied image before Windows starts for the first time.</p>
          </div>
          <Button variant="ghost" size="icon" onClick={onClose} aria-label="Close"><X /></Button>
        </div>

        <div className="space-y-5 overflow-y-auto p-5">
          <div className="space-y-2">
            <Label htmlFor="rv-name">Name</Label>
            <Input id="rv-name" value={draft.name} maxLength={100} placeholder="Why this value is set" onChange={(e) => set({ name: e.target.value })} />
          </div>

          <div className="grid grid-cols-2 gap-4">
            <div className="space-y-2">
              <Label htmlFor="rv-hive">Hive</Label>
              <Select id="rv-hive" value={draft.hive} onValueChange={(v) => set({ hive: v as SetupRegistryHive })} options={HIVE_OPTIONS} />
            </div>
            <div className="space-y-2">
              <Label htmlFor="rv-type">Type</Label>
              <Select id="rv-type" value={draft.type} onValueChange={(v) => set({ type: v as SetupRegistryValueType })} options={VALUE_TYPE_OPTIONS} />
            </div>
          </div>

          <div className="space-y-2">
            <Label htmlFor="rv-key">Key</Label>
            <Input id="rv-key" value={draft.key} maxLength={512} onChange={(e) => set({ key: e.target.value })} aria-describedby="rv-key-help" />
            <p id="rv-key-help" className="text-sm text-muted-foreground">
              Relative to the hive, for example Policies\Microsoft\FVE. Under HKLM SYSTEM, CurrentControlSet is mapped to the image&apos;s current control set. 32-bit software reads WOW6432Node keys; name them explicitly.
            </p>
          </div>

          <div className="space-y-2">
            <Label htmlFor="rv-value-name">Value name</Label>
            <Input id="rv-value-name" value={draft.valueName} maxLength={255} placeholder="(Default)" onChange={(e) => set({ valueName: e.target.value })} />
          </div>

          <div className="space-y-2">
            <Label htmlFor="rv-data">Data</Label>
            {draft.type === 'MultiString' ? (
              <>
                <textarea id="rv-data" className={TEXTAREA_CLASS} value={draft.data} onChange={(e) => set({ data: e.target.value.replace(/\r\n/g, '\n') })} aria-describedby="rv-data-help" />
                <p id="rv-data-help" className="text-sm text-muted-foreground">One string per line.</p>
              </>
            ) : (
              <Input id="rv-data" value={draft.data} onChange={(e) => set({ data: e.target.value })}
                placeholder={draft.type === 'DWord' || draft.type === 'QWord' ? 'Decimal, or hex starting with 0x' : undefined} />
            )}
          </div>

          {attempted && problems.length > 0 && (
            <ul role="alert" className="space-y-1">
              {problems.map((p) => <li key={p} className="text-sm text-destructive">{p}</li>)}
            </ul>
          )}
        </div>

        <div className="flex justify-end gap-2 border-t border-border px-5 py-3">
          <Button variant="secondary" onClick={onClose}>Cancel</Button>
          <Button onClick={apply}><Check />{value ? 'Apply' : 'Add'}</Button>
        </div>
      </div>
    </div>,
    document.body,
  );
}
