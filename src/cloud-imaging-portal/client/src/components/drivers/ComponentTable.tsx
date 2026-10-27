import React, { Fragment, useEffect, useState } from 'react';
import { Check, ChevronDown, ChevronRight, Info, Lock, Package, Trash2, Undo2 } from 'lucide-react';
import { Badge } from '../ui/badge.tsx';
import { Button } from '../ui/button.tsx';
import { Select } from '../ui/select.tsx';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '../ui/table.tsx';
import { Tooltip } from '../ui/tooltip.tsx';
import { actionLabel, rollBackText, vendorLabel, type DriverAction, type DriverComponentRow, type DriverVendor } from '../../lib/drivers.ts';
import { ComponentDetails, type ComponentHistoryContext } from './ComponentDetails.tsx';

const LOCKED_HINT = "The vendor's data only allows Skip for this component.";

const ACTION_HINT = 'Offline: drivers are added to Windows before it starts. Offline + Specialize: added offline, and the vendor installer runs during Windows setup for its app. Specialize: the vendor installer runs during Windows setup. Skip: not installed.';

/** Changes the table can ask for; absent for read-only users, who see plain text. */
export interface ComponentTableEditing {
  /** Lineage key of the row being saved, so its controls wait. */
  saving: string | null;
  onOverride: (component: DriverComponentRow, action: DriverAction) => void;
  onUndoOverride: (component: DriverComponentRow) => void;
  onApprove: (component: DriverComponentRow) => void;
  /** Removes a component the vendor no longer publishes. */
  onRemoveWithdrawn?: (component: DriverComponentRow) => void;
  /** Removes a custom component from every model it is on. */
  onRemoveCustom?: (component: DriverComponentRow) => void;
}

/** Where the action came from, in precedence order, with Approve for a component waiting for approval. */
function SourceCell({ component, editing, vendor }: { component: DriverComponentRow; editing?: ComponentTableEditing; vendor?: DriverVendor }): React.ReactElement {
  if (component.state === 'WaitingForApproval' && editing) {
    return (
      <Button variant="outline" aria-label={`Approve ${component.name}`} disabled={editing.saving === component.lineageKey} onClick={() => editing.onApprove(component)}><Check />Approve</Button>
    );
  }
  return <SourceText component={component} editing={editing} vendor={vendor} />;
}

function SourceText({ component, editing, vendor }: { component: DriverComponentRow; editing?: ComponentTableEditing; vendor?: DriverVendor }): React.ReactElement {
  switch (component.source) {
    case 'Locked':
      return <span>Locked</span>;
    case 'ReplacedByCustom':
      return <span className="text-muted-foreground">Replaced by custom</span>;
    case 'Custom': {
      const replaced = component.custom?.replacesName;
      const hint = replaced
        ? `Replaces ${replaced} on this model until you remove it.`
        : `Added alongside the ${vendor ? vendorLabel(vendor) : 'vendor'} components until you remove it.`;
      return (
        <span className="flex items-center gap-2">
          <Tooltip content={hint}>
            <span tabIndex={0} aria-label={hint} className="inline-flex shrink-0">
              <Package className="h-4 w-4 text-violet-600 dark:text-violet-400" aria-hidden="true" />
            </span>
          </Tooltip>
          <span>Custom</span>
        </span>
      );
    }
    case 'RolledBack': {
      const hint = component.rollBack && vendor ? rollBackText(component.rollBack, vendor) : 'Held at an earlier version.';
      return (
        <span className="flex items-center gap-2">
          <Tooltip content={hint}>
            <span tabIndex={0} aria-label={hint} className="inline-flex shrink-0">
              <Undo2 className="h-4 w-4 text-amber-600 dark:text-amber-400" aria-hidden="true" />
            </span>
          </Tooltip>
          <span>Rolled back</span>
        </span>
      );
    }
    case 'Rule':
      return (
        <span className="block min-w-0">
          <span className="block truncate" title={component.ruleName ?? undefined}>{component.ruleName}</span>
          <span className="block text-xs text-muted-foreground">Rule</span>
        </span>
      );
    case 'Override':
      return (
        <span className="flex items-center gap-2">
          <span>Override</span>
          {editing && (
            <Tooltip content="Undo override">
              <Button
                variant="ghost"
                size="icon"
                aria-label={`Undo override of ${component.name}`}
                disabled={editing.saving === component.lineageKey}
                onClick={() => editing.onUndoOverride(component)}
              >
                <Undo2 />
              </Button>
            </Tooltip>
          )}
        </span>
      );
    default:
      return <span className="text-muted-foreground">Default</span>;
  }
}

function ActionCell({ component, editing }: { component: DriverComponentRow; editing?: ComponentTableEditing }): React.ReactElement {
  const locked = component.source === 'Locked';
  const lock = locked && (
    <Tooltip content={LOCKED_HINT}>
      <span tabIndex={0} aria-label={LOCKED_HINT} className="inline-flex shrink-0 text-muted-foreground">
        <Lock className="h-4 w-4" aria-hidden="true" />
      </span>
    </Tooltip>
  );
  if (!editing) {
    return <span className="flex items-center gap-2">{lock}{actionLabel(component.action)}</span>;
  }
  return (
    <span className="flex items-center gap-2">
      {lock}
      <Select
        value={component.action}
        onValueChange={value => { if (value !== component.action) editing.onOverride(component, value as DriverAction); }}
        options={(locked ? [component.action] : component.allowedActions).map(a => ({ value: a, label: actionLabel(a) }))}
        disabled={locked || component.source === 'ReplacedByCustom' || component.allowedActions.length < 2 || editing.saving === component.lineageKey}
        aria-label={`Action for ${component.name}`}
        wrapperClassName="w-[176px] shrink-0"
      />
    </span>
  );
}

/**
 * Components of a model and release, every one listed (no paging), one line each with a chevron
 * for the details. With `editing`, the Action column is a dropdown of the allowed actions and an
 * override can be undone from the Source column.
 */
export function ComponentTable({ components, total, editing, history }: { components: DriverComponentRow[]; total: number; editing?: ComponentTableEditing; history?: ComponentHistoryContext }): React.ReactElement {
  // A link from the sync history (#lineageKey) opens its row; otherwise the first component waiting for approval.
  const [open, setOpen] = useState<string | null>(() => {
    const linked = decodeURIComponent(window.location.hash.slice(1));
    return (linked && components.some(c => c.lineageKey === linked) ? linked : null)
      ?? components.find(c => c.state === 'WaitingForApproval')?.lineageKey ?? null;
  });

  useEffect(() => {
    const linked = decodeURIComponent(window.location.hash.slice(1));
    if (linked) document.getElementById(`component-${linked}`)?.scrollIntoView?.({ block: 'center' });
  }, []);

  return (
    <div className="space-y-2">
      <Table className="table-fixed">
        <TableHeader>
          <TableRow>
            <TableHead className="w-[44px]"><span className="sr-only">Details</span></TableHead>
            <TableHead>Component</TableHead>
            <TableHead className="w-[120px]">Version</TableHead>
            <TableHead className="w-[110px]">Last updated</TableHead>
            <TableHead className="w-[232px]">
              <span className="inline-flex items-center gap-1">
                Action
                <Tooltip content={ACTION_HINT}>
                  <span tabIndex={0} aria-label={ACTION_HINT} className="inline-flex text-muted-foreground">
                    <Info className="h-3 w-3" aria-hidden="true" />
                  </span>
                </Tooltip>
              </span>
            </TableHead>
            <TableHead className="w-[190px]">Source</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {components.map(component => {
            const expanded = open === component.lineageKey;
            const detailsId = `component-details-${component.lineageKey}`;
            const waiting = component.state === 'WaitingForApproval';
            return (
              <Fragment key={component.lineageKey}>
                <TableRow id={`component-${component.lineageKey}`} className={`h-14 ${waiting ? 'bg-amber-50/60 dark:bg-amber-950/20' : ''} ${component.action === 'Skip' ? 'text-muted-foreground' : ''}`}>
                  <TableCell>
                    <button
                      type="button"
                      aria-expanded={expanded}
                      aria-controls={detailsId}
                      aria-label={`${expanded ? 'Hide' : 'Show'} details of ${component.name}`}
                      className="inline-flex rounded-md p-1 text-muted-foreground hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
                      onClick={() => setOpen(expanded ? null : component.lineageKey)}
                    >
                      {expanded ? <ChevronDown className="h-4 w-4" aria-hidden="true" /> : <ChevronRight className="h-4 w-4" aria-hidden="true" />}
                    </button>
                  </TableCell>
                  <TableCell>
                    <div className="flex min-w-0 items-center gap-2">
                      <span className="truncate font-medium" title={component.name}>{component.name}</span>
                      {component.state === 'WaitingForApproval' && <Badge variant="warning">New</Badge>}
                      {component.state === 'Withdrawn' && <Badge variant="muted" title="The vendor no longer publishes this component.">Withdrawn</Badge>}
                    </div>
                    <div className="truncate text-xs text-muted-foreground">
                      {component.custom
                        ? [`Uploaded by ${component.custom.uploadedBy ?? 'unknown'}`, component.category].filter(Boolean).join(', ')
                        : [component.componentId, component.category].filter(Boolean).join(', ')}
                    </div>
                  </TableCell>
                  <TableCell className="truncate" title={component.version ?? undefined}>{component.version}</TableCell>
                  <TableCell>{component.lastUpdated}</TableCell>
                  <TableCell><ActionCell component={component} editing={editing} /></TableCell>
                  <TableCell><SourceCell component={component} editing={editing} vendor={history?.vendor} /></TableCell>
                </TableRow>
                {expanded && (
                  <TableRow id={detailsId}>
                    <TableCell colSpan={6} className="bg-muted/30 p-0">
                      <ComponentDetails component={component} history={component.custom ? undefined : history}>
                        {component.custom && editing?.onRemoveCustom && (
                          <div className="flex items-center gap-4 rounded-md border border-border px-4 py-3">
                            <p className="min-w-0 flex-1 text-sm text-muted-foreground">A custom component stays on its models until you remove it.</p>
                            <Button variant="outline" aria-label={`Remove custom component ${component.name}`} onClick={() => editing.onRemoveCustom?.(component)}><Trash2 />Remove custom component</Button>
                          </div>
                        )}
                        {component.state === 'Withdrawn' && editing?.onRemoveWithdrawn && (
                          <div className="flex items-center gap-4 rounded-md border border-border px-4 py-3">
                            <p className="min-w-0 flex-1 text-sm text-muted-foreground">The vendor no longer publishes this component. Devices keep getting the last stored version until you remove it.</p>
                            <Button variant="outline" aria-label={`Remove ${component.name}`} disabled={editing.saving === component.lineageKey} onClick={() => editing.onRemoveWithdrawn?.(component)}><Trash2 />Remove</Button>
                          </div>
                        )}
                      </ComponentDetails>
                    </TableCell>
                  </TableRow>
                )}
              </Fragment>
            );
          })}
        </TableBody>
      </Table>
      <p className="text-sm text-muted-foreground">{`${components.length} of ${total} components`}</p>
    </div>
  );
}
