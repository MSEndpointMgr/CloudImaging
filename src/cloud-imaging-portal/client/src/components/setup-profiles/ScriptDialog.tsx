import { useState } from 'react';
import { createPortal } from 'react-dom';
import { X } from 'lucide-react';
import { Button } from '../ui/button.tsx';
import { CodeTextArea } from '../ui/code-text-area.tsx';
import { Input } from '../ui/input.tsx';
import { Label } from '../ui/label.tsx';
import { Select } from '../ui/select.tsx';
import {
  DEFAULT_TIMEOUT_MINUTES,
  SCRIPT_RUNS_OPTIONS,
  TIMEOUT_OPTIONS,
  scriptSizeLabel,
  validateScript,
  type SetupScript,
  type SetupScriptPhase,
} from '../../lib/setupProfiles.ts';

/**
 * Add or edit one script. Done only closes the dialog; the profile's own Save creates the version.
 * Problems use the same rules as Imaging Core and show once the operator tries to close with Done.
 */
export function ScriptDialog({ script, others, onClose, onApply }: {
  /** The script to edit; omit to add one. */
  script?: SetupScript;
  /** The profile's other scripts, to keep names unique. */
  others: SetupScript[];
  onClose: () => void;
  onApply: (script: SetupScript) => void;
}): React.ReactElement {
  const [draft, setDraft] = useState<SetupScript>(
    () => script ?? { id: null, name: '', runs: 'WinPE', order: 0, timeoutMinutes: DEFAULT_TIMEOUT_MINUTES, content: '' },
  );
  const [attempted, setAttempted] = useState(false);
  const set = (patch: Partial<SetupScript>) => setDraft((d) => ({ ...d, ...patch }));
  const problems = validateScript(draft, others);

  const apply = () => {
    setAttempted(true);
    if (problems.length > 0) return;
    onApply({ ...draft, name: draft.name.trim() });
  };

  return createPortal(
    <div className="fixed inset-0 z-40 flex items-center justify-center bg-black/60 p-4" role="dialog" aria-modal="true" aria-labelledby="script-dialog-title">
      <div className="flex max-h-full w-full max-w-3xl flex-col rounded-lg border border-border bg-background shadow-xl">
        <div className="flex items-start justify-between gap-3 border-b border-border p-5">
          <div className="space-y-1">
            <h2 id="script-dialog-title" className="text-base font-semibold">{script ? 'Edit script' : 'Add script'}</h2>
            <p className="text-sm text-muted-foreground">Runs as SYSTEM on every device that gets this profile.</p>
          </div>
          <Button variant="ghost" size="icon" onClick={onClose} aria-label="Close"><X /></Button>
        </div>

        <div className="space-y-5 overflow-y-auto p-5">
          <div className="grid grid-cols-[minmax(0,1fr)_200px_140px] gap-4">
            <div className="space-y-2">
              <Label htmlFor="script-name">Name</Label>
              <Input id="script-name" value={draft.name} maxLength={100} onChange={(e) => set({ name: e.target.value })} />
            </div>
            <div className="space-y-2">
              <Label htmlFor="script-runs">Runs</Label>
              <Select id="script-runs" value={draft.runs} onValueChange={(v) => set({ runs: v as SetupScriptPhase })} options={SCRIPT_RUNS_OPTIONS} />
            </div>
            <div className="space-y-2">
              <Label htmlFor="script-timeout">Timeout</Label>
              <Select id="script-timeout" value={String(draft.timeoutMinutes)} onValueChange={(v) => set({ timeoutMinutes: Number(v) })} options={TIMEOUT_OPTIONS} />
            </div>
          </div>

          <div className="space-y-2">
            <Label htmlFor="script-content">PowerShell</Label>
            <p id="script-content-help" className="text-xs text-muted-foreground">
              Exit code 0 is success. In WinPE, the Windows drive is <span className="font-mono">$env:CI_WINDOWS_DRIVE</span>. Scripts cannot restart the device.
            </p>
            <CodeTextArea id="script-content" label="PowerShell" value={draft.content} onChange={(content) => set({ content })} describedBy="script-content-help script-content-size" />
            <p id="script-content-size" className="text-xs text-muted-foreground">
              {scriptSizeLabel(draft.content)}. Do not put secrets in scripts: everyone with access to this page can read them.
            </p>
          </div>

          {attempted && problems.length > 0 && (
            <ul role="alert" className="space-y-1">
              {problems.map((p) => <li key={p} className="text-sm text-destructive">{p}</li>)}
            </ul>
          )}
        </div>

        <div className="flex justify-end gap-2 border-t border-border px-5 py-3">
          <Button variant="secondary" onClick={onClose}>Cancel</Button>
          <Button onClick={apply}>Done</Button>
        </div>
      </div>
    </div>,
    document.body,
  );
}
