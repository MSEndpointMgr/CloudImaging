import React, { useEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { Lightbulb, Plus, X } from 'lucide-react';
import { Button } from '../ui/button.tsx';
import { EmptyState } from '../ui/empty-state.tsx';
import { Skeleton } from '../ui/skeleton.tsx';
import { actionLabel, createRule, fetchRuleSuggestions, ruleMatchLabel, scopeLabel, type DriverRule } from '../../lib/drivers.ts';
import { useDialogFocus } from './useDialogFocus.ts';

/** Suggested Skip rules. Nothing is added until the operator presses Add on one. */
export function SuggestedRulesDialog({ onClose, onAdded }: { onClose: () => void; onAdded: (rule: DriverRule) => void }): React.ReactElement {
  const dialogRef = useRef<HTMLDivElement>(null);
  useDialogFocus(dialogRef, onClose);
  const [suggestions, setSuggestions] = useState<DriverRule[] | null>(null);
  const [adding, setAdding] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    fetchRuleSuggestions()
      .then(list => { if (!cancelled) setSuggestions(list); })
      .catch((err: unknown) => { if (!cancelled) { setSuggestions([]); setError(err instanceof Error ? err.message : 'Could not load suggested rules.'); } });
    return () => { cancelled = true; };
  }, []);

  const add = async (suggestion: DriverRule) => {
    setAdding(suggestion.name);
    setError(null);
    try {
      const rule = await createRule({ name: suggestion.name, scope: suggestion.scope, match: suggestion.match, value: suggestion.value, action: suggestion.action });
      setSuggestions(list => (list ?? []).filter(s => s.name !== suggestion.name));
      onAdded(rule);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not add the rule.');
    } finally {
      setAdding(null);
    }
  };

  return createPortal(
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 p-4">
      <div ref={dialogRef} role="dialog" aria-modal="true" aria-labelledby="suggested-rules-title" className="flex max-h-full w-full max-w-2xl flex-col rounded-lg border border-border bg-background shadow-xl">
        <div className="flex items-start justify-between gap-3 border-b border-border p-5">
          <div className="space-y-1">
            <h2 id="suggested-rules-title" className="text-base font-semibold">Suggested rules</h2>
            <p className="text-sm text-muted-foreground">Rules that skip vendor telemetry and service components. Add the ones you want.</p>
          </div>
          <Button variant="ghost" size="icon" onClick={onClose} aria-label="Close"><X /></Button>
        </div>

        <div className="space-y-3 overflow-y-auto p-5">
          {suggestions === null ? (
            <><Skeleton className="h-12 w-full" /><Skeleton className="h-12 w-full" /></>
          ) : suggestions.length === 0 && !error ? (
            <EmptyState icon={Lightbulb} title="Every suggested rule is added" />
          ) : (
            <ul className="divide-y divide-border rounded-lg border border-border">
              {suggestions.map(s => (
                <li key={s.name} className="flex items-center gap-4 px-4 py-3">
                  <div className="min-w-0 flex-1">
                    <p className="truncate text-sm font-medium" title={s.name}>{s.name}</p>
                    <p className="truncate text-sm text-muted-foreground">{`${actionLabel(s.action)}, ${scopeLabel(s.scope)}, ${ruleMatchLabel(s)}. Matches ${s.hits} ${s.hits === 1 ? 'component' : 'components'}.`}</p>
                  </div>
                  <Button variant="outline" onClick={() => void add(s)} loading={adding === s.name} aria-label={`Add ${s.name}`}><Plus />Add</Button>
                </li>
              ))}
            </ul>
          )}
          {error && <p role="alert" className="text-sm text-destructive">{error}</p>}
        </div>

        <div className="flex justify-end gap-2 border-t border-border px-5 py-3">
          <Button variant="secondary" onClick={onClose}>Close</Button>
        </div>
      </div>
    </div>,
    document.body,
  );
}
