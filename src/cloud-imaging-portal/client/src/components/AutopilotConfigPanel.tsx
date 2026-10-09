import { useCallback, useEffect, useMemo, useState } from 'react';
import { BadgeCheck, Pencil, Plus, Tag, Trash2 } from 'lucide-react';
import { apiFetch, apiFetchWithRetry, extractErrorDetail } from '../lib/apiClient.ts';
import {
  TEMPLATE_TOKENS, resolveTemplate, validateGroupTagDefinition,
  type GroupTagDefinition, type GroupTagKind,
} from '../lib/autopilot.ts';
import { useToast } from '../context/toastContext.tsx';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from './ui/card.tsx';
import { Switch } from './ui/switch.tsx';
import { Button } from './ui/button.tsx';
import { Input } from './ui/input.tsx';
import { Label } from './ui/label.tsx';
import { Select } from './ui/select.tsx';
import { Badge } from './ui/badge.tsx';
import { EmptyState } from './ui/empty-state.tsx';
import { Tooltip } from './ui/tooltip.tsx';
import { TableSkeletonRows } from './ui/skeleton.tsx';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from './ui/table.tsx';
import { ConfirmImpactDialog } from './ConfirmImpactDialog.tsx';

interface LocationEntry {
  locationId: string;
  name: string;
  region?: string | null;
  countryCode?: string | null;
}

interface Props {
  enabled: boolean;
  groupTagRequired: boolean;
  expiryDays: number;
  retentionDays: number;
  onEnabledChange: (value: boolean) => void;
  onGroupTagRequiredChange: (value: boolean) => void;
  onExpiryDaysChange: (value: number) => void;
  onRetentionDaysChange: (value: number) => void;
}

/**
 * Autopilot registration settings. The three switches/limits are part of the portal
 * configuration and saved with Save Configuration; the group tag list is managed immediately.
 */
export function AutopilotConfigPanel(props: Props): React.ReactElement {
  return (
    <div className="space-y-6">
      <Card>
        <CardHeader>
          <div className="flex items-center gap-2">
            <BadgeCheck className="h-5 w-5 text-primary" aria-hidden="true" />
            <CardTitle>Autopilot registration</CardTitle>
          </div>
          <CardDescription>
            Lets technicians send a device&apos;s Windows Autopilot hardware hash from the boot media. Approvers decide in the
            portal, and approved devices are imported into Intune. Importing needs the Microsoft Graph
            DeviceManagementServiceConfig.ReadWrite.All permission on the Imaging Core identity.
          </CardDescription>
        </CardHeader>
        <CardContent className="space-y-4">
          <SettingRow
            title="Allow Autopilot registration"
            description="When enabled, boot media offers Register with Autopilot so technicians can submit a device's hardware hash for approval."
          >
            <Switch checked={props.enabled} onCheckedChange={props.onEnabledChange} label="Allow Autopilot registration" />
          </SettingRow>
          <SettingRow
            title="Require a group tag"
            description="When enabled, approvers must select a group tag before a device can be imported."
          >
            <Switch checked={props.groupTagRequired} onCheckedChange={props.onGroupTagRequiredChange} label="Require a group tag" />
          </SettingRow>
          <div className="space-y-2 rounded-md border border-border bg-muted/30 p-4">
            <Label htmlFor="autopilotPendingExpiryDays">Pending requests expire after (days)</Label>
            <p className="text-sm text-muted-foreground">Undecided requests expire and their hardware hash is deleted. 1 to 90 days.</p>
            <Input
              id="autopilotPendingExpiryDays"
              type="number"
              min={1}
              max={90}
              value={props.expiryDays}
              onChange={(e) => props.onExpiryDaysChange(Number(e.target.value))}
              className="w-40"
            />
          </div>
          <div className="space-y-2 rounded-md border border-border bg-muted/30 p-4">
            <Label htmlFor="autopilotRetentionDays">Keep handled requests for (days)</Label>
            <p className="text-sm text-muted-foreground">
              Imported, rejected and expired requests stay in the Autopilot registration history report for auditing, then are deleted. 30 to 3650 days.
            </p>
            <Input
              id="autopilotRetentionDays"
              type="number"
              min={30}
              max={3650}
              value={props.retentionDays}
              onChange={(e) => props.onRetentionDaysChange(Number(e.target.value))}
              className="w-40"
            />
          </div>
        </CardContent>
      </Card>

      <GroupTagManager />
    </div>
  );
}

function SettingRow({ title, description, children }: { title: string; description: string; children: React.ReactNode }): React.ReactElement {
  return (
    <div className="flex items-center justify-between gap-4 rounded-md border border-border bg-muted/30 p-4">
      <div>
        <p className="text-sm font-medium">{title}</p>
        <p className="mt-0.5 text-sm text-muted-foreground">{description}</p>
      </div>
      {children}
    </div>
  );
}

interface Draft {
  id: string | null;
  name: string;
  kind: GroupTagKind;
  value: string;
  description: string;
}

const EMPTY_DRAFT: Draft = { id: null, name: '', kind: 'Static', value: '', description: '' };

const KIND_OPTIONS = [
  { value: 'Static', label: 'Static', description: 'The value is used as typed.' },
  { value: 'Template', label: 'Template', description: 'Built from the device location when the approver reviews it.' },
];

function GroupTagManager(): React.ReactElement {
  const { notify } = useToast();
  const [tags, setTags] = useState<GroupTagDefinition[] | null>(null);
  const [locations, setLocations] = useState<LocationEntry[]>([]);
  const [previewLocationId, setPreviewLocationId] = useState('');
  const [draft, setDraft] = useState<Draft | null>(null);
  const [saving, setSaving] = useState(false);
  const [deleting, setDeleting] = useState<GroupTagDefinition | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      const [tagRes, locRes] = await Promise.all([
        apiFetchWithRetry('/api/autopilot/group-tags', { credentials: 'include' }),
        apiFetchWithRetry('/api/locations', { credentials: 'include' }),
      ]);
      if (!tagRes.ok) throw new Error(await extractErrorDetail(tagRes, 'Could not load group tags.'));
      setTags(await tagRes.json() as GroupTagDefinition[]);
      if (locRes.ok) {
        const data = await locRes.json() as LocationEntry[];
        setLocations(data);
        setPreviewLocationId((current) => current || data.find((l) => l.region && l.countryCode)?.locationId || data[0]?.locationId || '');
      }
      setLoadError(null);
    } catch (err) {
      setTags((current) => current ?? []);
      setLoadError(err instanceof Error ? err.message : 'Could not reach the server.');
    }
  }, []);

  useEffect(() => { void load(); }, [load]);

  const previewLocation = locations.find((l) => l.locationId === previewLocationId);
  const draftError = draft ? (draft.name.trim() ? validateGroupTagDefinition(draft.kind, draft.value) : 'A name is required.') : null;
  const draftPreview = useMemo(() => {
    if (!draft || draft.kind !== 'Template' || !draft.value.trim()) return null;
    return resolveTemplate(draft.value.trim(), {
      name: previewLocation?.name, region: previewLocation?.region, countryCode: previewLocation?.countryCode,
    });
  }, [draft, previewLocation]);

  const save = async () => {
    if (!draft || draftError) return;
    setSaving(true);
    try {
      const res = await apiFetch(draft.id ? `/api/autopilot/group-tags/${draft.id}` : '/api/autopilot/group-tags', {
        method: draft.id ? 'PUT' : 'POST',
        headers: { 'Content-Type': 'application/json' },
        credentials: 'include',
        body: JSON.stringify({
          name: draft.name.trim(), kind: draft.kind, value: draft.value.trim(), description: draft.description.trim() || undefined,
        }),
      });
      if (!res.ok) {
        notify({ status: 'error', title: 'Could not save group tag', description: await extractErrorDetail(res, 'The group tag was not saved.') });
        return;
      }
      notify({ status: 'success', title: `Group tag "${draft.name.trim()}" saved.` });
      setDraft(null);
      await load();
    } catch {
      notify({ status: 'error', title: 'Network error.', description: 'Could not reach the server.' });
    } finally {
      setSaving(false);
    }
  };

  const remove = async () => {
    if (!deleting) return;
    setSaving(true);
    try {
      const res = await apiFetch(`/api/autopilot/group-tags/${deleting.id}`, { method: 'DELETE', credentials: 'include' });
      if (!res.ok) {
        notify({ status: 'error', title: 'Could not delete group tag', description: await extractErrorDetail(res, 'The group tag was not deleted.') });
        return;
      }
      setDeleting(null);
      await load();
    } catch {
      notify({ status: 'error', title: 'Network error.', description: 'Could not reach the server.' });
    } finally {
      setSaving(false);
    }
  };

  const insertToken = (token: string) => {
    setDraft((d) => (d ? { ...d, value: `${d.value}${token}` } : d));
  };

  return (
    <Card>
      <CardHeader>
        <div className="flex items-center justify-between gap-4">
          <div className="space-y-1.5">
            <div className="flex items-center gap-2">
              <Tag className="h-5 w-5 text-primary" aria-hidden="true" />
              <CardTitle>Group tags</CardTitle>
            </div>
            <CardDescription>
              The only tags approvers can choose. Templates resolve from the device&apos;s location (region and country are set on
              the Locations page). Changes apply immediately.
            </CardDescription>
          </div>
          {!draft && (
            <Button onClick={() => setDraft({ ...EMPTY_DRAFT })}>
              <Plus /> Add group tag
            </Button>
          )}
        </div>
      </CardHeader>
      <CardContent className="space-y-4">
        {loadError && <p role="alert" className="text-sm text-destructive">{loadError}</p>}

        {draft && (
          <div className="space-y-4 rounded-md border border-border bg-muted/30 p-4">
            <div className="grid gap-4 sm:grid-cols-2">
              <div className="space-y-2">
                <Label htmlFor="tag-name">Name</Label>
                <Input id="tag-name" value={draft.name} maxLength={64} onChange={(e) => setDraft({ ...draft, name: e.target.value })} placeholder="e.g. Regional standard" />
              </div>
              <div className="space-y-2">
                <Label htmlFor="tag-kind">Type</Label>
                <Select id="tag-kind" value={draft.kind} onValueChange={(v) => setDraft({ ...draft, kind: v as GroupTagKind })} options={KIND_OPTIONS} />
              </div>
            </div>
            <div className="space-y-2">
              <Label htmlFor="tag-value">{draft.kind === 'Template' ? 'Template' : 'Tag value'}</Label>
              <Input
                id="tag-value"
                value={draft.value}
                maxLength={256}
                onChange={(e) => setDraft({ ...draft, value: e.target.value })}
                placeholder={draft.kind === 'Template' ? '{Region}-{CountryCode}-STD' : 'e.g. Kiosk'}
                className="font-mono"
              />
              {draft.kind === 'Template' && (
                <div className="flex flex-wrap items-center gap-2">
                  <span className="text-sm text-muted-foreground">Insert:</span>
                  {TEMPLATE_TOKENS.map((t) => (
                    <Button key={t.token} type="button" variant="outline" onClick={() => insertToken(t.token)}>
                      {t.label}
                    </Button>
                  ))}
                </div>
              )}
            </div>
            <div className="space-y-2">
              <Label htmlFor="tag-description">Description (optional)</Label>
              <Input id="tag-description" value={draft.description} maxLength={256} onChange={(e) => setDraft({ ...draft, description: e.target.value })} />
            </div>

            {draft.kind === 'Template' && (
              <div className="flex flex-wrap items-end gap-4">
                <div className="space-y-2">
                  <Label htmlFor="tag-preview-location">Preview with location</Label>
                  <Select
                    id="tag-preview-location"
                    value={previewLocationId}
                    onValueChange={setPreviewLocationId}
                    options={locations.map((l) => ({ value: l.locationId, label: l.name, description: [l.region, l.countryCode].filter(Boolean).join(' \u00b7 ') || 'No region or country set' }))}
                    placeholder={locations.length === 0 ? 'No locations defined' : 'Choose a location'}
                    disabled={locations.length === 0}
                    wrapperClassName="w-64"
                  />
                </div>
                <p className="pb-2 text-sm" aria-live="polite">
                  {draftPreview?.value
                    ? <>Resolves to <span className="font-mono font-medium">{draftPreview.value}</span></>
                    : <span className="text-muted-foreground">{draftPreview?.reason ?? 'Enter a template to see a preview.'}</span>}
                </p>
              </div>
            )}

            {draftError && draft.value.trim() && <p className="text-sm text-destructive">{draftError}</p>}
            <div className="flex justify-end gap-2">
              <Button variant="outline" onClick={() => setDraft(null)} disabled={saving}>Cancel</Button>
              <Button onClick={() => void save()} disabled={saving || !!draftError} loading={saving}>
                {draft.id ? 'Save changes' : 'Add group tag'}
              </Button>
            </div>
          </div>
        )}

        <div className="overflow-hidden rounded-md border border-border">
          <Table>
            <TableHeader>
              <TableRow className="hover:bg-transparent">
                <TableHead>Name</TableHead>
                <TableHead>Type</TableHead>
                <TableHead>Value</TableHead>
                <TableHead className="w-[96px]"><span className="sr-only">Actions</span></TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {tags === null && <TableSkeletonRows columns={4} rows={2} />}
              {tags?.length === 0 && (
                <TableRow className="hover:bg-transparent">
                  <TableCell colSpan={4} className="p-0">
                    <EmptyState icon={Tag} title="No group tags yet" description="Without group tags, approvers can only import devices untagged." />
                  </TableCell>
                </TableRow>
              )}
              {tags?.map((t) => (
                <TableRow key={t.id}>
                  <TableCell>
                    <span className="text-sm font-medium">{t.name}</span>
                    {t.description && <span className="block text-xs text-muted-foreground">{t.description}</span>}
                  </TableCell>
                  <TableCell><Badge variant={t.kind === 'Template' ? 'info' : 'muted'}>{t.kind}</Badge></TableCell>
                  <TableCell className="font-mono text-sm">{t.value}</TableCell>
                  <TableCell>
                    <div className="flex gap-1">
                      <Tooltip content="Edit group tag">
                        <Button variant="ghost" size="icon" aria-label={`Edit group tag ${t.name}`}
                          onClick={() => setDraft({ id: t.id, name: t.name, kind: t.kind, value: t.value, description: t.description ?? '' })}>
                          <Pencil />
                        </Button>
                      </Tooltip>
                      <Tooltip content="Delete group tag">
                        <Button variant="ghost" size="icon" aria-label={`Delete group tag ${t.name}`}
                          className="text-muted-foreground hover:bg-destructive/10 hover:text-destructive"
                          onClick={() => setDeleting(t)}>
                          <Trash2 />
                        </Button>
                      </Tooltip>
                    </div>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </div>
      </CardContent>

      {deleting && (
        <ConfirmImpactDialog
          copy={{
            confirmTitle: `Delete group tag "${deleting.name}"?`,
            impact: 'Approvers can no longer choose it. Devices already imported keep the tag they were given, and Intune is not changed.',
            confirmLabel: 'Delete',
            destructive: true,
          }}
          busy={saving}
          onCancel={() => setDeleting(null)}
          onConfirm={() => void remove()}
          titleId="delete-group-tag-title"
        />
      )}
    </Card>
  );
}
