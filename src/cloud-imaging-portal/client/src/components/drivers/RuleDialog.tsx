import React, { useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { Plus, Save, X } from 'lucide-react';
import { Button } from '../ui/button.tsx';
import { Input } from '../ui/input.tsx';
import { Label } from '../ui/label.tsx';
import { Select } from '../ui/select.tsx';
import {
  DRIVER_ACTIONS, RULE_MATCHES, actionLabel, createRule, scopeLabel, updateRule, validateRule,
  type DriverAction, type DriverCategory, type DriverRule, type DriverRuleInput, type DriverRuleMatch, type DriverRuleScope,
} from '../../lib/drivers.ts';
import { useDialogFocus } from './useDialogFocus.ts';

/** Add or edit a rule. Field problems show under their fields once the operator tries to save. */
export function RuleDialog({ rule, rules, scopes, categories, onClose, onSaved }: {
  /** The rule to edit; omit to add one. */
  rule?: DriverRule;
  /** Every rule, so a name in use is caught before saving. */
  rules: DriverRule[];
  /** Scopes that can be chosen: all vendors and each supported vendor. */
  scopes: DriverRuleScope[];
  /** Categories seen in components, offered for a category rule. */
  categories: DriverCategory[];
  onClose: () => void;
  onSaved: (rule: DriverRule) => void;
}): React.ReactElement {
  const dialogRef = useRef<HTMLDivElement>(null);
  useDialogFocus(dialogRef, onClose);
  const [draft, setDraft] = useState<DriverRuleInput>(() => rule
    ? { name: rule.name, scope: rule.scope, match: rule.match, value: rule.value, action: rule.action }
    : { name: '', scope: 'AllVendors', match: 'NamePattern', value: '', action: 'Skip' });
  const [attempted, setAttempted] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const set = (patch: Partial<DriverRuleInput>) => setDraft(d => ({ ...d, ...patch }));
  const problems = validateRule(draft, rules.filter(r => r.ruleId !== rule?.ruleId));
  const scopeOptions = [...new Set<DriverRuleScope>([...scopes, draft.scope])].map(s => ({ value: s, label: scopeLabel(s) }));
  const categoryNames = [...new Set(categories.filter(c => draft.scope === 'AllVendors' || c.vendor === draft.scope).map(c => c.category))].sort();
  if (draft.match === 'Category' && draft.value && !categoryNames.includes(draft.value)) categoryNames.unshift(draft.value);

  const save = async () => {
    setAttempted(true);
    if (problems.name || problems.value) return;
    setSaving(true);
    setError(null);
    try {
      const input = { ...draft, name: draft.name.trim(), value: draft.value.trim() };
      onSaved(rule ? await updateRule(rule.ruleId, input) : await createRule(input));
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not save the rule.');
      setSaving(false);
    }
  };

  return createPortal(
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 p-4">
      <div ref={dialogRef} role="dialog" aria-modal="true" aria-labelledby="rule-dialog-title" className="flex max-h-full w-full max-w-lg flex-col rounded-lg border border-border bg-background shadow-xl">
        <div className="flex items-start justify-between gap-3 border-b border-border p-5">
          <div className="space-y-1">
            <h2 id="rule-dialog-title" className="text-base font-semibold break-words">{rule ? `Edit ${rule.name}` : 'Add rule'}</h2>
            <p className="text-sm text-muted-foreground">Sets the action of every matching component on every model in scope.</p>
          </div>
          <Button variant="ghost" size="icon" onClick={onClose} aria-label="Close"><X /></Button>
        </div>

        <div className="space-y-5 overflow-y-auto p-5">
          <div className="space-y-2">
            <Label htmlFor="rule-name">Name</Label>
            <Input id="rule-name" value={draft.name} maxLength={100} onChange={e => set({ name: e.target.value })} aria-invalid={attempted && !!problems.name} aria-describedby={attempted && problems.name ? 'rule-name-error' : undefined} />
            {attempted && problems.name && <p id="rule-name-error" className="text-sm text-destructive">{problems.name}</p>}
          </div>

          <div className="grid grid-cols-2 gap-4">
            <div className="space-y-2">
              <Label htmlFor="rule-scope">Applies to</Label>
              <Select id="rule-scope" value={draft.scope} onValueChange={v => set({ scope: v as DriverRuleScope })} options={scopeOptions} />
            </div>
            <div className="space-y-2">
              <Label htmlFor="rule-action">Action</Label>
              <Select id="rule-action" value={draft.action} onValueChange={v => set({ action: v as DriverAction })} options={DRIVER_ACTIONS.map(a => ({ value: a, label: actionLabel(a) }))} />
            </div>
          </div>

          <div className="grid grid-cols-2 gap-4">
            <div className="space-y-2">
              <Label htmlFor="rule-match">Match</Label>
              <Select id="rule-match" value={draft.match} onValueChange={v => set({ match: v as DriverRuleMatch, value: '' })} options={RULE_MATCHES.map(m => ({ value: m.value, label: m.label }))} />
            </div>
            <div className="space-y-2">
              <Label htmlFor="rule-value">{draft.match === 'Category' ? 'Category' : draft.match === 'ComponentId' ? 'Component ID' : 'Name pattern'}</Label>
              {draft.match === 'Category' ? (
                <Select id="rule-value" value={draft.value} onValueChange={v => set({ value: v })} options={categoryNames.map(c => ({ value: c, label: c }))} placeholder="Choose a category" />
              ) : (
                <Input id="rule-value" value={draft.value} maxLength={draft.match === 'ComponentId' ? 80 : 200} onChange={e => set({ value: e.target.value })}
                  aria-invalid={attempted && !!problems.value} aria-describedby={draft.match === 'NamePattern' ? 'rule-value-help' : undefined} />
              )}
            </div>
          </div>
          {draft.match === 'NamePattern' && <p id="rule-value-help" className="text-sm text-muted-foreground">Use * as a wildcard.</p>}
          {attempted && problems.value && <p className="text-sm text-destructive">{problems.value}</p>}
          {error && <p role="alert" className="text-sm text-destructive">{error}</p>}
        </div>

        <div className="flex justify-end gap-2 border-t border-border px-5 py-3">
          <Button variant="secondary" onClick={onClose}>Cancel</Button>
          <Button onClick={() => void save()} loading={saving}>{rule ? <><Save />Save</> : <><Plus />Add rule</>}</Button>
        </div>
      </div>
    </div>,
    document.body,
  );
}
