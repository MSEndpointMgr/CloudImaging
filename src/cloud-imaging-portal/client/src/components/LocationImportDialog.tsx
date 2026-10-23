import * as React from 'react';
import { Check, CircleAlert, Upload, X } from 'lucide-react';
import { Button } from './ui/button.tsx';
import { Badge } from './ui/badge.tsx';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from './ui/table.tsx';
import { countryName } from '../lib/countries.ts';
import { regionCodeFor, type RegionsView } from '../lib/regions.ts';
import {
  buildImportPlan,
  parseLocationImport,
  LocationImportError,
  type ImportPlan,
} from '../lib/locationImport.ts';
import { cn } from '../lib/utils.ts';

interface LocationImportDialogProps {
  existingNames: readonly string[];
  regionsView: RegionsView | null;
  /** Creates one location. Resolves true when it was created. */
  onCreate: (name: string, countryCode: string | null) => Promise<boolean>;
  onClose: () => void;
  /** Called after a run that created at least one location, so the page can reload. */
  onImported: () => void;
}

const SAMPLE = `[
  { "name": "Seattle HQ", "countryCode": "US" },
  { "name": "Copenhagen", "countryCode": "DK" }
]`;

/**
 * Bulk-creates locations from a JSON file.
 *
 * The file carries only name and country. Region is not importable because it is derived from the
 * country by the region mapping, so importing one could only contradict that mapping.
 *
 * Nothing is sent until the operator has seen the plan. The catalog does not enforce unique names,
 * so an unchecked import of the same file twice would quietly double every location.
 */
export function LocationImportDialog({
  existingNames,
  regionsView,
  onCreate,
  onClose,
  onImported,
}: LocationImportDialogProps): React.ReactElement {
  const inputRef = React.useRef<HTMLInputElement>(null);
  const [fileName, setFileName] = React.useState<string | null>(null);
  const [plan, setPlan] = React.useState<ImportPlan | null>(null);
  const [parseError, setParseError] = React.useState<string | null>(null);
  const [busy, setBusy] = React.useState(false);
  const [result, setResult] = React.useState<{ created: number; failed: number } | null>(null);

  const regionFor = (countryCode: string | null): string | null =>
    regionsView && countryCode ? regionCodeFor(regionsView.regions, regionsView.countries, countryCode) : null;

  const handleFile = async (file: File): Promise<void> => {
    setFileName(file.name);
    setResult(null);
    try {
      setPlan(buildImportPlan(parseLocationImport(await file.text()), existingNames));
      setParseError(null);
    } catch (err) {
      setPlan(null);
      setParseError(err instanceof LocationImportError ? err.message : 'That file could not be read.');
    }
  };

  const handleImport = async (): Promise<void> => {
    if (!plan || plan.createCount === 0 || busy) return;
    setBusy(true);
    let created = 0;
    let failed = 0;
    // Sequential: the catalog is small, and a burst of parallel writes would make a partial
    // failure much harder to describe afterwards.
    for (const entry of plan.entries.filter(e => e.action === 'create')) {
      if (await onCreate(entry.name, entry.countryCode)) created++;
      else failed++;
    }
    setBusy(false);
    setResult({ created, failed });
    if (created > 0) onImported();
  };

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 p-4" role="presentation">
      <div
        role="dialog"
        aria-modal="true"
        aria-labelledby="location-import-title"
        className="flex max-h-[85vh] w-full max-w-3xl flex-col rounded-xl border border-border bg-card shadow-xl"
      >
        <div className="flex items-start justify-between gap-4 border-b border-border p-6">
          <div className="flex items-center gap-2">
            <Upload className="h-5 w-5 text-primary" aria-hidden="true" />
            <h2 id="location-import-title" className="text-lg font-semibold">Import locations</h2>
          </div>
          <Button variant="ghost" size="icon" aria-label="Close" onClick={onClose}>
            <X />
          </Button>
        </div>

        <div className="min-h-0 flex-1 overflow-y-auto p-6">
          <p className="text-sm text-muted-foreground">
            A JSON file with a name and an ISO country code for each location. The region follows
            from the country, so it is not part of the file.
          </p>
          <pre className="mt-3 overflow-x-auto rounded-md border border-border bg-muted/30 p-3 text-xs">{SAMPLE}</pre>

          <div className="mt-4 flex flex-wrap items-center gap-3">
            <input
              ref={inputRef}
              type="file"
              accept="application/json,.json"
              className="sr-only"
              onChange={event => {
                const file = event.target.files?.[0];
                // Reset so re-picking the same file after an edit still raises change.
                event.target.value = '';
                if (file) void handleFile(file);
              }}
            />
            <Button variant="outline" onClick={() => inputRef.current?.click()} disabled={busy}>
              <Upload /> Choose file
            </Button>
            {fileName && <span className="text-sm text-muted-foreground">{fileName}</span>}
          </div>

          {parseError && (
            <p className="mt-4 flex items-start gap-2 rounded-md border border-destructive/50 bg-destructive/10 p-3 text-sm text-destructive">
              <CircleAlert className="mt-0.5 h-4 w-4 shrink-0" aria-hidden="true" />
              {parseError}
            </p>
          )}

          {plan && (
            <>
              <div className="mt-5 flex flex-wrap items-center gap-2">
                <Badge variant="success">{plan.createCount} to add</Badge>
                {plan.duplicateCount > 0 && <Badge variant="muted">{plan.duplicateCount} already exist</Badge>}
                {plan.invalidCount > 0 && <Badge variant="destructive">{plan.invalidCount} cannot be added</Badge>}
              </div>

              <div className="mt-3 overflow-hidden rounded-md border border-border">
                <Table>
                  <TableHeader>
                    <TableRow className="hover:bg-transparent">
                      <TableHead>Name</TableHead>
                      <TableHead>Country</TableHead>
                      <TableHead>Region</TableHead>
                      <TableHead>Result</TableHead>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {plan.entries.map(entry => (
                      <TableRow key={entry.index} className={cn(entry.action !== 'create' && 'text-muted-foreground')}>
                        <TableCell className="font-medium">{entry.name || <span className="italic">No name</span>}</TableCell>
                        <TableCell>{entry.countryCode ? countryName(entry.countryCode) : <span className="text-muted-foreground">-</span>}</TableCell>
                        <TableCell>{regionFor(entry.countryCode) ?? <span className="text-muted-foreground">-</span>}</TableCell>
                        <TableCell>
                          {entry.action === 'create'
                            ? <span className="text-emerald-600 dark:text-emerald-400">Will be added</span>
                            : <span title={entry.reason}>{entry.reason}</span>}
                        </TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              </div>
            </>
          )}

          {result && (
            <p className="mt-4 flex items-start gap-2 rounded-md border border-border bg-muted/30 p-3 text-sm">
              <Check className="mt-0.5 h-4 w-4 shrink-0 text-emerald-600 dark:text-emerald-400" aria-hidden="true" />
              Added {result.created} {result.created === 1 ? 'location' : 'locations'}
              {result.failed > 0 && `, ${result.failed} failed`}.
            </p>
          )}
        </div>

        <div className="flex justify-end gap-2 border-t border-border p-6">
          <Button variant="secondary" onClick={onClose}>{result ? 'Close' : 'Cancel'}</Button>
          {!result && (
            <Button onClick={handleImport} disabled={!plan || plan.createCount === 0} loading={busy}>
              Add {plan?.createCount ?? 0} {plan?.createCount === 1 ? 'location' : 'locations'}
            </Button>
          )}
        </div>
      </div>
    </div>
  );
}
