import React, { useCallback, useEffect, useState } from 'react';
import { Info, ListChecks, Pencil, Plus, RefreshCw, Sparkles, Trash2 } from 'lucide-react';
import { Button } from '../ui/button.tsx';
import { EmptyState } from '../ui/empty-state.tsx';
import { TableSkeletonRows } from '../ui/skeleton.tsx';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '../ui/table.tsx';
import { Tooltip } from '../ui/tooltip.tsx';
import { ConfirmImpactDialog } from '../ConfirmImpactDialog.tsx';
import { useAuth } from '../../context/authContext.tsx';
import { useToast } from '../../context/toastContext.tsx';
import {
  actionLabel, deleteRule, fetchCategories, fetchRules, fetchVendors, ruleMatchLabel, scopeLabel,
  type DriverCategory, type DriverRule, type DriverRuleScope, type DriverVendor,
} from '../../lib/drivers.ts';
import { DeploymentFailureCard } from './DeploymentFailureCard.tsx';
import { RuleDialog } from './RuleDialog.tsx';
import { SuggestedRulesDialog } from './SuggestedRulesDialog.tsx';

const PRECEDENCE = 'A model override wins over rules, and vendor rules win over rules for all vendors. Within a layer an ID rule wins over a name rule, which wins over a category rule. A component the vendor withdraws stays at its last version until you remove it from the model.';

/** Rules tab: rules with their hits, suggested rules, and the Deployment failure card. */
export function RulesTab(): React.ReactElement {
  const { canManageDrivers } = useAuth();
  const { notify } = useToast();
  const [rules, setRules] = useState<DriverRule[] | null>(null);
  const [categories, setCategories] = useState<DriverCategory[]>([]);
  const [vendors, setVendors] = useState<DriverVendor[]>([]);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [editing, setEditing] = useState<DriverRule | 'new' | null>(null);
  const [suggesting, setSuggesting] = useState(false);
  const [deleting, setDeleting] = useState<DriverRule | null>(null);
  const [busy, setBusy] = useState(false);

  const load = useCallback(async () => {
    setLoadError(null);
    try {
      const [ruleList, categoryList, vendorList] = await Promise.all([fetchRules(), fetchCategories(), fetchVendors()]);
      setRules(ruleList);
      setCategories(categoryList);
      setVendors(vendorList.filter(v => v.supported && v.vendor !== 'Lenovo').map(v => v.vendor));
    } catch (err) {
      setLoadError(err instanceof Error ? err.message : 'Could not load rules.');
    }
  }, []);

  useEffect(() => { void load(); }, [load]);

  const closeDialog = useCallback(() => setEditing(null), []);
  const closeSuggestions = useCallback(() => setSuggesting(false), []);

  // Hits depend on every rule, so the list is read again after any change.
  const saved = (rule: DriverRule, verb: string) => {
    setEditing(null);
    notify({ status: 'success', title: `Rule ${rule.name} ${verb}.`, description: 'Affected driver sets are built 2 minutes after the last change.' });
    void load();
  };

  const remove = async () => {
    if (!deleting) return;
    setBusy(true);
    try {
      await deleteRule(deleting.ruleId);
      notify({ status: 'success', title: `Rule ${deleting.name} deleted.` });
      setDeleting(null);
      await load();
    } catch (err) {
      notify({ status: 'error', title: 'Could not delete the rule', description: err instanceof Error ? err.message : undefined });
    } finally {
      setBusy(false);
    }
  };

  const scopes: DriverRuleScope[] = ['AllVendors', ...vendors.filter((v): v is 'Hp' | 'Dell' => v === 'Hp' || v === 'Dell')];

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-center gap-2">
        <p className="flex items-center gap-2 text-sm text-muted-foreground">
          Rules decide what happens to matching components on every model in scope.
          <Tooltip content={PRECEDENCE}>
            <span tabIndex={0} aria-label={PRECEDENCE} className="inline-flex text-muted-foreground">
              <Info className="h-4 w-4" aria-hidden="true" />
            </span>
          </Tooltip>
        </p>
        {canManageDrivers && (
          <div className="ml-auto flex items-center gap-2">
            <Button variant="outline" onClick={() => setSuggesting(true)}><Sparkles />Suggested rules</Button>
            <Button onClick={() => setEditing('new')}><Plus />Add rule</Button>
          </div>
        )}
      </div>

      {loadError ? (
        <EmptyState icon={RefreshCw} title="Could not load rules" description={loadError} action={<Button variant="outline" onClick={() => void load()}><RefreshCw />Try again</Button>} />
      ) : rules === null ? (
        <Table><TableBody><TableSkeletonRows columns={6} rows={3} /></TableBody></Table>
      ) : rules.length === 0 ? (
        <EmptyState
          icon={ListChecks}
          title="No rules yet"
          description="Every component uses its vendor default until a rule or an override changes it."
          action={canManageDrivers ? <Button variant="outline" onClick={() => setSuggesting(true)}><Sparkles />Suggested rules</Button> : undefined}
        />
      ) : (
        <Table className="table-fixed">
          <TableHeader>
            <TableRow>
              <TableHead className="w-[24%]">Name</TableHead>
              <TableHead className="w-[12%]">Applies to</TableHead>
              <TableHead>Matches</TableHead>
              <TableHead className="w-[14%]">Action</TableHead>
              <TableHead className="w-[8%] text-right">Hits</TableHead>
              {canManageDrivers && <TableHead className="w-[96px]"><span className="sr-only">Actions</span></TableHead>}
            </TableRow>
          </TableHeader>
          <TableBody>
            {rules.map(rule => (
              <TableRow key={rule.ruleId}>
                <TableCell className="truncate font-medium" title={rule.name}>{rule.name}</TableCell>
                <TableCell>{scopeLabel(rule.scope)}</TableCell>
                <TableCell className="truncate" title={ruleMatchLabel(rule)}>{ruleMatchLabel(rule)}</TableCell>
                <TableCell>{actionLabel(rule.action)}</TableCell>
                <TableCell className="text-right tabular-nums">{rule.hits}</TableCell>
                {canManageDrivers && (
                  <TableCell>
                    <div className="flex justify-end gap-1">
                      <Tooltip content="Edit rule">
                        <Button variant="ghost" size="icon" aria-label={`Edit ${rule.name}`} onClick={() => setEditing(rule)}><Pencil /></Button>
                      </Tooltip>
                      <Tooltip content="Delete rule">
                        <Button variant="ghost" size="icon" className="hover:text-destructive" aria-label={`Delete ${rule.name}`} onClick={() => setDeleting(rule)}><Trash2 /></Button>
                      </Tooltip>
                    </div>
                  </TableCell>
                )}
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}

      {rules !== null && (
        <DeploymentFailureCard
          vendors={vendors}
          categories={categories}
          readOnly={!canManageDrivers}
          onChanged={(vendor, next) => setCategories(current => [...current.filter(c => c.vendor !== vendor), ...next])}
          onError={message => notify({ status: 'error', title: 'Could not save the critical categories', description: message })}
        />
      )}

      {editing && (
        <RuleDialog
          rule={editing === 'new' ? undefined : editing}
          rules={rules ?? []}
          scopes={scopes}
          categories={categories}
          onClose={closeDialog}
          onSaved={rule => saved(rule, editing === 'new' ? 'added' : 'saved')}
        />
      )}
      {suggesting && <SuggestedRulesDialog onClose={closeSuggestions} onAdded={rule => { notify({ status: 'success', title: `Rule ${rule.name} added.` }); void load(); }} />}
      {deleting && (
        <ConfirmImpactDialog
          copy={{
            confirmTitle: `Delete ${deleting.name}?`,
            impact: deleting.hits === 0
              ? 'No component uses this rule, so no driver set changes.'
              : `${deleting.hits === 1 ? 'The component this rule decides goes back to the next rule or its vendor default.' : `The ${deleting.hits} components this rule decides go back to the next rule or their vendor default.`} Affected driver sets are built again 2 minutes after the change.`,
            confirmLabel: 'Delete',
            destructive: true,
          }}
          busy={busy}
          onCancel={() => setDeleting(null)}
          onConfirm={() => void remove()}
        />
      )}
    </div>
  );
}
