import React, { useState } from 'react';
import { ChevronDown } from 'lucide-react';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '../ui/card.tsx';
import { Checkbox } from '../ui/checkbox.tsx';
import { setCriticalCategories, vendorLabel, type DriverCategory, type DriverVendor } from '../../lib/drivers.ts';

/** One vendor's categories as a collapsible checkbox group; each change is saved at once. */
function CategoryPicker({ vendor, categories, readOnly, onSaved, onError }: {
  vendor: DriverVendor;
  categories: DriverCategory[];
  readOnly: boolean;
  onSaved: (vendor: DriverVendor, categories: DriverCategory[]) => void;
  onError: (message: string) => void;
}): React.ReactElement {
  const [open, setOpen] = useState(false);
  const [saving, setSaving] = useState(false);
  const critical = categories.filter(c => c.critical).map(c => c.category);
  const name = vendorLabel(vendor);
  const summary = critical.length === 0 ? 'None' : critical.join(', ');
  const groupId = `critical-${vendor}`;

  const toggle = async (category: string, on: boolean) => {
    const next = on ? [...critical, category] : critical.filter(c => c !== category);
    setSaving(true);
    try {
      onSaved(vendor, await setCriticalCategories(vendor, next));
    } catch (err) {
      onError(err instanceof Error ? err.message : 'Could not save the critical categories.');
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="space-y-2 py-3">
      <div className="flex items-center gap-4">
        <div className="w-32 shrink-0 text-sm font-medium">{name}</div>
        <button
          type="button"
          aria-expanded={open}
          aria-controls={groupId}
          aria-label={`${name} critical categories: ${summary}`}
          disabled={categories.length === 0}
          onClick={() => setOpen(o => !o)}
          onKeyDown={e => { if (e.key === 'Escape') setOpen(false); }}
          className="flex h-8 min-w-0 flex-1 items-center justify-between gap-2 rounded-md border border-input bg-background pl-3 pr-2 text-left text-sm shadow-sm hover:bg-accent/40 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50"
        >
          <span className="truncate">{categories.length === 0 ? 'No categories yet' : summary}</span>
          <ChevronDown className="h-4 w-4 shrink-0 text-muted-foreground" aria-hidden="true" />
        </button>
      </div>
      {open && (
        <div id={groupId} role="group" aria-label={`${name} categories`} className="ml-36 grid grid-cols-2 gap-2 rounded-md border border-border p-3">
          {categories.map(c => (
            <Checkbox
              key={c.category}
              checked={c.critical}
              onCheckedChange={on => void toggle(c.category, on)}
              label={c.category}
              disabled={readOnly || saving}
            />
          ))}
        </div>
      )}
    </div>
  );
}

/**
 * Deployment failure: per vendor, the categories whose failure fails the whole deployment. The
 * choice is copied to each session when imaging is assigned, so no driver set is rebuilt.
 */
export function DeploymentFailureCard({ vendors, categories, readOnly, onChanged, onError }: {
  vendors: DriverVendor[];
  categories: DriverCategory[];
  readOnly: boolean;
  onChanged: (vendor: DriverVendor, categories: DriverCategory[]) => void;
  onError: (message: string) => void;
}): React.ReactElement {
  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-base">Deployment failure</CardTitle>
        <CardDescription>Choose the driver categories whose failure fails the whole deployment. Other failures complete with warnings.</CardDescription>
      </CardHeader>
      <CardContent className="divide-y divide-border">
        {vendors.map(vendor => (
          <CategoryPicker
            key={vendor}
            vendor={vendor}
            categories={categories.filter(c => c.vendor === vendor)}
            readOnly={readOnly}
            onSaved={onChanged}
            onError={onError}
          />
        ))}
      </CardContent>
    </Card>
  );
}
