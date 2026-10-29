import React from 'react';
import { CircleAlert, Undo2 } from 'lucide-react';
import { Button } from '../ui/button.tsx';
import { Select, type SelectOption } from '../ui/select.tsx';
import { Tooltip } from '../ui/tooltip.tsx';
import { cn } from '../../lib/utils.ts';
import { NO_DRIVERS, formatBytes, noMatchText, optionLabel, optionValue, vendorLabel, type DriverAssignmentPreview } from '../../lib/drivers.ts';

/** The options of one row: per vendor, the matched model first in its group (its set in testing after it), then No drivers. */
function driverSetOptions(preview: DriverAssignmentPreview, matchedModelId: string | null): SelectOption[] {
  const vendors = [...new Set(preview.options.map(o => o.vendor))];
  const grouped = vendors.flatMap(vendor => preview.options
    .filter(o => o.vendor === vendor)
    .sort((a, b) => (a.modelId === matchedModelId ? -1 : 0) - (b.modelId === matchedModelId ? -1 : 0))
    .map(o => ({ value: optionValue(o), label: optionLabel(o), group: vendorLabel(o.vendor), meta: formatBytes(o.maxDownloadBytes) })));
  return [...grouped, { value: NO_DRIVERS, label: 'No drivers' }];
}

/**
 * The Start imaging overlay's driver lines: one per distinct outcome, "2 devices: HP EliteBook
 * 840 G11", "1 device: no drivers (chosen manually)", "1 device: no match".
 */
export function StartImagingDriverLines({ sessionIds, preview, choices }: {
  sessionIds: string[];
  preview: DriverAssignmentPreview;
  choices: Readonly<Record<string, string>>;
}): React.ReactElement | null {
  const names = new Map(preview.options.map(o => [optionValue(o), optionLabel(o)]));
  const lines: { label: string; manual: boolean; count: number }[] = [];
  for (const sessionId of sessionIds) {
    const choice = choices[sessionId];
    const matched = preview.sessions.find(s => s.sessionId === sessionId)?.matchedModelId ?? null;
    const manual = choice !== undefined;
    const value = choice ?? matched;
    const label = value === NO_DRIVERS ? 'no drivers' : value ? (names.get(value) ?? 'no match') : 'no match';
    const line = lines.find(l => l.label === label && l.manual === manual);
    if (line) line.count += 1;
    else lines.push({ label, manual, count: 1 });
  }
  if (lines.length === 0) return null;
  return (
    <div className="space-y-2">
      <p className="text-xs leading-relaxed text-muted-foreground">Driver set:</p>
      <ul className="list-disc space-y-1 pl-4 text-xs leading-relaxed text-muted-foreground">
        {lines.map(line => (
          <li key={`${line.label}-${String(line.manual)}`}>
            {`${line.count} device${line.count === 1 ? '' : 's'}: ${line.label}`}
            {line.manual && <span className="font-medium"> (chosen manually)</span>}
          </li>
        ))}
      </ul>
    </div>
  );
}

/**
 * One coupled device's cell in the Driver set column: the matched model, or a prompt with the
 * reason when nothing matched; a different choice can be undone back to the match.
 */
export function DriverSetCell({ sessionId, serial, manufacturer, preview, choice, onChoose }: {
  sessionId: string;
  serial: string;
  manufacturer: string;
  preview: DriverAssignmentPreview;
  /** The manual choice, when it differs from the match. */
  choice: string | undefined;
  onChoose: (value: string | undefined) => void;
}): React.ReactElement {
  if (!preview.release) {
    return <span className="text-sm text-muted-foreground">The OS image has no Windows release</span>;
  }
  const session = preview.sessions.find(s => s.sessionId === sessionId);
  const matched = session?.matchedModelId ?? '';
  const value = choice ?? matched;
  // Only Operators get testing options, so only they are pointed at a set in testing.
  const testingName = preview.options.find(o => o.testing && o.modelId === session?.testingModelId)?.displayName;
  const reason = !matched && session ? noMatchText(session, manufacturer, preview.release, preview.architecture, testingName) : null;
  const changed = choice !== undefined && choice !== matched;
  return (
    <div className="flex min-w-0 items-center gap-2">
      <Select
        value={value}
        onValueChange={next => onChoose(next === matched ? undefined : next)}
        options={driverSetOptions(preview, session?.matchedModelId ?? session?.testingModelId ?? null)}
        placeholder="No match, choose a set"
        aria-label={`Driver set for ${serial}`}
        className={cn(!value && 'border-amber-500 dark:border-amber-400')}
        wrapperClassName="min-w-0 flex-1"
      />
      {reason && !value && (
        <Tooltip content={reason}>
          <span tabIndex={0} aria-label={reason} className="inline-flex shrink-0">
            <CircleAlert className="h-4 w-4 text-amber-600 dark:text-amber-400" aria-hidden="true" />
          </span>
        </Tooltip>
      )}
      {changed && (
        <Tooltip content="Use the matched set">
          <Button variant="ghost" size="icon" aria-label="Use the matched set" onClick={() => onChoose(undefined)}>
            <Undo2 />
          </Button>
        </Tooltip>
      )}
    </div>
  );
}
