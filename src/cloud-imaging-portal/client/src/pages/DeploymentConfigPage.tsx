import { useState, useEffect } from 'react';
import { Lock, Link as LinkIcon, RotateCcw, SlidersHorizontal } from 'lucide-react';
import { SaveBar } from '../components/SaveBar.tsx';
import { unsavedRail, type UnsavedChange } from '../lib/unsavedChanges.ts';
import { partitioningChanges } from '../lib/partitioningScheme.ts';
import { usePartitioningScheme } from '../hooks/usePartitioningScheme.ts';
import { PreFlightAuthorizationToggle } from '../components/PreFlightAuthorizationToggle.tsx';
import { BootMediaCertPanel, type CertMetadata } from '../components/BootMediaCertPanel.tsx';
import { PartitioningSchemePanel } from '../components/PartitioningSchemePanel.tsx';
import { UpdateCheckPanel } from '../components/UpdateCheckPanel.tsx';
import { AutopilotConfigPanel } from '../components/AutopilotConfigPanel.tsx';
import { SectionCard, SettingRow } from '../components/setup-profiles/SectionCard.tsx';
import { UndoChangeButton } from '../components/ui/unsaved-change.tsx';
import { apiFetch, apiFetchWithRetry } from '../lib/apiClient.ts';
import { cn } from '../lib/utils';
import { Button, type ButtonStatus } from '../components/ui/button.tsx';
import { Input } from '../components/ui/input.tsx';
import { Label } from '../components/ui/label.tsx';
import { Tooltip } from '../components/ui/tooltip.tsx';
import { Card, CardContent, CardHeader, CardTitle, CardDescription } from '../components/ui/card.tsx';
import { PageLoading } from '../components/PageLoading.tsx';
import { useToast } from '../context/toastContext.tsx';
import type { ToastContextValue } from '../context/toastContext.tsx';
import { rolesFromAccount } from '../context/authContext.tsx';
import { getMsalInstance } from '../lib/msal.ts';
import { getCertExpiryWarning, hasCertExpiryWarningBeenShown, markCertExpiryWarningShown } from '../lib/certExpiry.ts';
import { isPreFlightSettingsValid } from '../lib/preflight.ts';

interface PortalConfig {
  devicePreFlightAuthorizationEnabled: boolean;
  preFlightRequireAutopilotPresence: boolean;
  preFlightRequireUefiFirmware: boolean;
  preFlightRequireSecureBoot: boolean;
  preFlightRequireTpm20: boolean;
  sasTokenUrlExpiryMinutes: number;
  bootImageSasExpiryMinutes: number;
  certValidityPeriodDays: number;
  clockSkewToleranceSeconds: number;
  sessionHistoryRetentionDays: number;
  updateCheckEnabled: boolean;
  autopilotRegistrationEnabled: boolean;
  autopilotGroupTagRequired: boolean;
  autopilotPendingExpiryDays: number;
  autopilotRetentionDays: number;
  setupProfileVersionRetention: number;
}

type CertMeta = CertMetadata;

type TabKey = 'certificates' | 'security' | 'preflight' | 'autopilot' | 'partitioning' | 'misc';

const TABS: { key: TabKey; label: string }[] = [
  { key: 'certificates', label: 'Certificates' },
  { key: 'security',     label: 'Security' },
  { key: 'preflight',    label: 'Preflight' },
  { key: 'autopilot',    label: 'Autopilot' },
  { key: 'partitioning', label: 'Partitioning' },
  { key: 'misc',         label: 'Miscellaneous' },
];

/** True when the two configs have identical persisted field values (no unsaved changes). */
function portalConfigEquals(a: PortalConfig, b: PortalConfig): boolean {
  return CONFIG_KEYS.every((key) => a[key] === b[key]);
}

/** Every saved field, with the name and tab the save bar uses to point at it. */
const FIELD_META: Record<keyof PortalConfig, { label: string; tab: TabKey }> = {
  devicePreFlightAuthorizationEnabled: { label: 'Require pre-flight authorization', tab: 'preflight' },
  preFlightRequireAutopilotPresence: { label: 'Require Autopilot presence', tab: 'preflight' },
  preFlightRequireUefiFirmware: { label: 'Require UEFI firmware mode', tab: 'preflight' },
  preFlightRequireSecureBoot: { label: 'Require Secure Boot', tab: 'preflight' },
  preFlightRequireTpm20: { label: 'Require TPM 2.0', tab: 'preflight' },
  sasTokenUrlExpiryMinutes: { label: 'OS image download link expiry', tab: 'misc' },
  bootImageSasExpiryMinutes: { label: 'Boot image download link expiry', tab: 'misc' },
  certValidityPeriodDays: { label: 'Certificate validity period', tab: 'security' },
  clockSkewToleranceSeconds: { label: 'Clock skew tolerance', tab: 'security' },
  sessionHistoryRetentionDays: { label: 'Session history retention', tab: 'misc' },
  updateCheckEnabled: { label: 'Check GitHub for new releases', tab: 'misc' },
  autopilotRegistrationEnabled: { label: 'Allow Autopilot registration', tab: 'autopilot' },
  autopilotGroupTagRequired: { label: 'Require a group tag', tab: 'autopilot' },
  autopilotPendingExpiryDays: { label: 'Pending requests expire after', tab: 'autopilot' },
  autopilotRetentionDays: { label: 'Keep handled requests for', tab: 'autopilot' },
  setupProfileVersionRetention: { label: 'Versions kept per profile', tab: 'misc' },
};

const CONFIG_KEYS = Object.keys(FIELD_META) as (keyof PortalConfig)[];

const TAB_LABEL = Object.fromEntries(TABS.map((t) => [t.key, t.label])) as Record<TabKey, string>;

/** Why the configuration cannot be saved as it stands, or null. */
function configProblem(config: PortalConfig): string | null {
  if (!isPreFlightSettingsValid(config)) return 'Select at least one pre-flight requirement, or turn pre-flight authorization off.';
  if (!Number.isInteger(config.autopilotPendingExpiryDays) || config.autopilotPendingExpiryDays < 1 || config.autopilotPendingExpiryDays > 90) {
    return 'Autopilot pending requests must expire after 1 to 90 days.';
  }
  if (!Number.isInteger(config.autopilotRetentionDays) || config.autopilotRetentionDays < 30 || config.autopilotRetentionDays > 3650) {
    return 'Handled Autopilot requests must be kept for 30 to 3650 days.';
  }
  if (!Number.isInteger(config.setupProfileVersionRetention) || config.setupProfileVersionRetention < 5 || config.setupProfileVersionRetention > 100) {
    return 'Keep between 5 and 100 versions per setup profile.';
  }
  return null;
}

/** Reads a problem-details `detail`/`title` from an error response, falling back to a default message. */
async function extractError(res: Response, fallback: string): Promise<string> {
  try {
    const body = await res.json() as { detail?: string; title?: string };
    return body.detail ?? body.title ?? fallback;
  } catch {
    return fallback;
  }
}

/**
 * Surfaces a one-time-per-bucket toast as the active boot media certificate approaches
 * expiry (30/14/7 day thresholds, then daily). Administrator-only (Technicians never
 * receive certificate metadata from `/api/cert/active`, so `meta` is never populated for
 * them; this check is an explicit extra guard on top of that).
 *
 * Module-level (not a component-scoped closure) and reads the account/roles directly
 * from the MSAL instance rather than `useAuth()`, so it's safe to call from `load()`,
 * which itself only runs once on mount (`useEffect(..., [])`).
 */
function maybeWarnCertExpiry(meta: CertMeta, notify: ToastContextValue['notify']): void {
  const account = getMsalInstance().getActiveAccount() ?? getMsalInstance().getAllAccounts()[0] ?? null;
  const isAdministrator = rolesFromAccount(account).includes('CloudImaging.Administrator');
  if (!isAdministrator || !account || !meta.expiresAt || !meta.thumbprintDisplay) return;
  const warning = getCertExpiryWarning(meta.expiresAt);
  if (!warning) return;
  const accountKey = account.homeAccountId || account.username;
  if (hasCertExpiryWarningBeenShown(accountKey, meta.thumbprintDisplay, warning.bucket)) return;
  notify({ status: warning.urgent ? 'error' : 'info', title: warning.title, description: warning.description });
  markCertExpiryWarningShown(accountKey, meta.thumbprintDisplay, warning.bucket);
}

/**
 * Deployment configuration page.
 * Groups the deployment settings into tabs: Certificates (boot media certificate
 * management), Security (certificate & token validation lifetimes), Preflight,
 * and Miscellaneous.
 */
export default function DeploymentConfigPage(): React.ReactElement {
  const { notify, update } = useToast();
  const [activeTab, setActiveTab] = useState<TabKey>('certificates');
  const DEFAULT_CONFIG: PortalConfig = {
    devicePreFlightAuthorizationEnabled: false,
    preFlightRequireAutopilotPresence: false,
    preFlightRequireUefiFirmware: false,
    preFlightRequireSecureBoot: false,
    preFlightRequireTpm20: false,
    sasTokenUrlExpiryMinutes:  240,
    bootImageSasExpiryMinutes: 120,
    certValidityPeriodDays:    365,
    clockSkewToleranceSeconds: 30,
    sessionHistoryRetentionDays: 90,
    updateCheckEnabled: false,
    autopilotRegistrationEnabled: false,
    autopilotGroupTagRequired: false,
    autopilotPendingExpiryDays: 7,
    autopilotRetentionDays: 365,
    setupProfileVersionRetention: 25,
  };
  const [config, setConfig] = useState<PortalConfig>(DEFAULT_CONFIG);
  /** Snapshot of the config as last loaded/saved. Used to detect unsaved changes. */
  const [savedConfig, setSavedConfig] = useState<PortalConfig>(DEFAULT_CONFIG);
  const isDirty = !portalConfigEquals(config, savedConfig);
  const partitioning = usePartitioningScheme();
  const [certMeta, setCertMeta] = useState<CertMeta | null>(null);
  const [loading, setLoading]   = useState(true);
  const [saveStatus, setSaveStatus] = useState<ButtonStatus>('idle');

  const changes: UnsavedChange[] = [
    ...CONFIG_KEYS.filter((key) => config[key] !== savedConfig[key])
      .map((key) => ({ label: FIELD_META[key].label, scope: TAB_LABEL[FIELD_META[key].tab] })),
    ...partitioningChanges(partitioning.scheme, partitioning.savedScheme, TAB_LABEL.partitioning),
  ];
  const blockedReason = isDirty ? configProblem(config) : null;
  const changed = (key: keyof PortalConfig) => config[key] !== savedConfig[key];
  const undo = (key: keyof PortalConfig) => setConfig(c => ({ ...c, [key]: savedConfig[key] }));

  const load = async () => {
    setLoading(true);
    try {
      const [cfgRes, certRes] = await Promise.all([
        apiFetchWithRetry('/api/portal-config', { credentials: 'include' }),
        apiFetchWithRetry('/api/cert/active',   { credentials: 'include' }),
      ]);
      if (cfgRes.ok) {
        // Spread over defaults so settings added after this deployment's last save still have values.
        const data = { ...DEFAULT_CONFIG, ...(await cfgRes.json() as Partial<PortalConfig>) };
        setConfig(data);
        setSavedConfig(data);
      }
      if (certRes.ok) {
        const meta = await certRes.json() as CertMeta;
        setCertMeta(meta);
      }
    } catch { /* use defaults */ }
    finally { setLoading(false); }
  };

  useEffect(() => { void load(); }, []);

  // Runs whenever certMeta changes (i.e. after every load()/onCertChanged refresh).
  // kept as a separate effect (rather than called inline from load()) so `notify` can be
  // listed as a real, correctly-tracked dependency instead of load() closing over it.
  useEffect(() => {
    if (certMeta) maybeWarnCertExpiry(certMeta, notify);
  }, [certMeta, notify]);

  // Saves the portal configuration and the partitioning scheme, whichever has changes.
  const save = async (): Promise<boolean> => {
    if (blockedReason) return false;
    setSaveStatus('loading');
    const toastId = notify({ status: 'loading', title: 'Saving configuration…' });
    const fail = (description: string) => {
      setSaveStatus('error');
      update(toastId, { status: 'error', title: 'Could not save configuration', description });
      setTimeout(() => setSaveStatus('idle'), 1600);
      return false;
    };
    try {
      if (isDirty) {
        const res = await apiFetch('/api/portal-config', {
          method: 'PUT',
          headers: { 'Content-Type': 'application/json' },
          credentials: 'include',
          body: JSON.stringify(config),
        });
        if (!res.ok && res.status !== 204) return fail(await extractError(res, 'Failed to save configuration.'));
        setSavedConfig(config);
      }
      if (partitioning.isDirty) {
        try {
          await partitioning.save();
        } catch (err) {
          return fail(err instanceof Error ? err.message : 'The partitioning scheme was not saved.');
        }
      }
      setSaveStatus('success');
      update(toastId, { status: 'success', title: 'Configuration saved', description: 'Deployment settings have been updated.' });
      setTimeout(() => setSaveStatus('idle'), 1600);
      return true;
    } catch {
      return fail('A network error occurred. Please try again.');
    }
  };

  if (loading) return <PageLoading />;

  const numField = (
    key: keyof PortalConfig,
    min: number,
    max: number,
    label: string,
    description: string,
    defaultValue: number,
  ) => (
    // `h-full` + `mt-auto` keep the inputs on one line across a row: descriptions wrap to
    // different heights, so without it each input floats at its own description's baseline.
    <div className={cn('flex h-full flex-col gap-2 rounded-md border border-border bg-muted/30 p-4', unsavedRail(changed(key)))}>
      <Label htmlFor={key}>{label}</Label>
      <p className="text-sm text-muted-foreground">{description}</p>
      <div className="mt-auto flex items-center gap-2 pt-1">
        <Input
          id={key}
          type="number"
          min={min}
          max={max}
          value={config[key] as number}
          onChange={e => setConfig(c => ({ ...c, [key]: Number(e.target.value) }))}
          className="w-40"
        />
        <Tooltip content={`Reset to ${defaultValue}`}>
          <Button
            type="button"
            variant="ghost"
            size="icon"
            onClick={() => setConfig(c => ({ ...c, [key]: defaultValue }))}
            disabled={(config[key] as number) === defaultValue}
            aria-label={`Reset ${label} to default`}
          >
            <RotateCcw />
          </Button>
        </Tooltip>
        {changed(key) && <UndoChangeButton label={FIELD_META[key].label} onUndo={() => undo(key)} />}
      </div>
    </div>
  );

  return (
    <div className="space-y-6">
      {/* Tab bar */}
      <div className="border-b border-border">
        <nav className="flex flex-wrap gap-1" role="tablist" aria-label="Configuration areas">
          {TABS.map(t => (
            <button
              key={t.key}
              type="button"
              role="tab"
              aria-selected={activeTab === t.key}
              onClick={() => setActiveTab(t.key)}
              className={cn(
                '-mb-px border-b-2 px-4 py-2 text-sm font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring',
                activeTab === t.key
                  ? 'border-primary text-foreground'
                  : 'border-transparent text-muted-foreground hover:text-foreground',
              )}
            >
              {t.label}
            </button>
          ))}
        </nav>
      </div>

      {/* Certificates */}
      {activeTab === 'certificates' && (
        <BootMediaCertPanel certMeta={certMeta} onCertChanged={() => void load()} />
      )}

      {/* Security */}
      {activeTab === 'security' && (
        <Card>
          <CardHeader>
            <div className="flex items-center gap-2">
              <Lock className="h-5 w-5 text-primary" aria-hidden="true" />
              <CardTitle>Certificate &amp; token validation</CardTitle>
            </div>
            <CardDescription>
              Controls how long issued certificates stay valid and how much clock
              difference is tolerated when validating tokens and certificates.
            </CardDescription>
          </CardHeader>
          <CardContent className="grid grid-cols-2 gap-6">
            {numField('certValidityPeriodDays', 30, 3650, 'Certificate validity period (days)',
              'The lifetime of newly issued boot media certificates before they expire and must be rotated.', DEFAULT_CONFIG.certValidityPeriodDays)}
            {numField('clockSkewToleranceSeconds', 0, 300, 'Clock skew tolerance (seconds)',
              'The permitted time difference between a device clock and the server clock when validating tokens and certificates.', DEFAULT_CONFIG.clockSkewToleranceSeconds)}
          </CardContent>
        </Card>
      )}

      {/* Preflight */}
      {activeTab === 'preflight' && (
        <PreFlightAuthorizationToggle
          settings={config}
          saved={savedConfig}
          onChange={next => setConfig(c => ({
            ...c,
            devicePreFlightAuthorizationEnabled: next.devicePreFlightAuthorizationEnabled,
            preFlightRequireAutopilotPresence: next.preFlightRequireAutopilotPresence,
            preFlightRequireUefiFirmware: next.preFlightRequireUefiFirmware,
            preFlightRequireSecureBoot: next.preFlightRequireSecureBoot,
            preFlightRequireTpm20: next.preFlightRequireTpm20,
          }))}
        />
      )}

      {/* Autopilot: switches are PortalConfig (the save bar); group tags save immediately. */}
      {activeTab === 'autopilot' && (
        <AutopilotConfigPanel
          enabled={config.autopilotRegistrationEnabled}
          groupTagRequired={config.autopilotGroupTagRequired}
          expiryDays={config.autopilotPendingExpiryDays}
          retentionDays={config.autopilotRetentionDays}
          saved={{
            enabled: savedConfig.autopilotRegistrationEnabled,
            groupTagRequired: savedConfig.autopilotGroupTagRequired,
            expiryDays: savedConfig.autopilotPendingExpiryDays,
            retentionDays: savedConfig.autopilotRetentionDays,
          }}
          onEnabledChange={v => setConfig(c => ({ ...c, autopilotRegistrationEnabled: v }))}
          onGroupTagRequiredChange={v => setConfig(c => ({ ...c, autopilotGroupTagRequired: v }))}
          onExpiryDaysChange={v => setConfig(c => ({ ...c, autopilotPendingExpiryDays: v }))}
          onRetentionDaysChange={v => setConfig(c => ({ ...c, autopilotRetentionDays: v }))}
        />
      )}

      {/* Partitioning: its draft is held by the page, so it survives switching tabs. */}
      {activeTab === 'partitioning' && (
        <PartitioningSchemePanel
          scheme={partitioning.scheme}
          savedScheme={partitioning.savedScheme}
          loading={partitioning.loading}
          onChange={partitioning.setScheme}
        />
      )}

      {/* Miscellaneous */}
      {activeTab === 'misc' && (
        <div className="space-y-6">
        <UpdateCheckPanel
          enabled={config.updateCheckEnabled}
          savedEnabled={savedConfig.updateCheckEnabled}
          onChange={v => setConfig(c => ({ ...c, updateCheckEnabled: v }))}
        />
        <Card>
          <CardHeader>
            <div className="flex items-center gap-2">
              <LinkIcon className="h-5 w-5 text-primary" aria-hidden="true" />
              <CardTitle>Download link expiry</CardTitle>
            </div>
            <CardDescription>
              Controls how long the temporary download links generated for image
              downloads remain usable.
            </CardDescription>
          </CardHeader>
          <CardContent className="grid grid-cols-2 gap-6">
            {numField('sasTokenUrlExpiryMinutes', 15, 1440, 'OS image download link expiry (minutes)',
              'How long a generated download link for an operating system image stays valid before it must be regenerated.', DEFAULT_CONFIG.sasTokenUrlExpiryMinutes)}
            {numField('bootImageSasExpiryMinutes', 15, 1440, 'Boot image download link expiry (minutes)',
              'How long a generated download link for a boot image stays valid before it must be regenerated.', DEFAULT_CONFIG.bootImageSasExpiryMinutes)}
            {numField('sessionHistoryRetentionDays', 1, 3650, 'Session history retention (days)',
              'How long completed device outcomes are kept for reporting before being purged.', DEFAULT_CONFIG.sessionHistoryRetentionDays)}
          </CardContent>
        </Card>
        <SectionCard icon={SlidersHorizontal} title="Setup profiles" description="History kept for each setup profile.">
          <SettingRow
            label="Versions kept per profile"
            help="Older versions are deleted at the next save. A version a running deployment still uses is always kept."
            htmlFor="setupProfileVersionRetention"
            changed={changed('setupProfileVersionRetention')}
            onUndo={() => undo('setupProfileVersionRetention')}
          >
            <Input
              id="setupProfileVersionRetention"
              type="number"
              min={5}
              max={100}
              value={config.setupProfileVersionRetention}
              onChange={e => setConfig(c => ({ ...c, setupProfileVersionRetention: Number(e.target.value) }))}
              className="w-[120px]"
            />
          </SettingRow>
        </SectionCard>
        </div>
      )}

      {/* One bar for the whole page, on every tab, so a change made on one tab is not lost from
          view on another. The certificate panel's actions take effect immediately. */}
      <SaveBar
        changes={changes}
        status={saveStatus}
        blockedReason={blockedReason}
        savedMessage="Configuration saved."
        onDiscard={() => { setConfig(savedConfig); partitioning.discard(); }}
        onSave={save}
      />
    </div>
  );
}

