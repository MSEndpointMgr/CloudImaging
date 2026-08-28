import { useState, useEffect } from 'react';
import { Lock, Link as LinkIcon } from 'lucide-react';
import { PreFlightAuthorizationToggle } from '../components/PreFlightAuthorizationToggle.tsx';
import { BootMediaCertPanel } from '../components/BootMediaCertPanel.tsx';
import { PartitioningSchemePanel } from '../components/PartitioningSchemePanel.tsx';
import { apiFetch, apiFetchWithRetry } from '../lib/apiClient.ts';
import { cn } from '../lib/utils';
import { Button, type ButtonStatus } from '../components/ui/button.tsx';
import { Input } from '../components/ui/input.tsx';
import { Label } from '../components/ui/label.tsx';
import { Card, CardContent, CardHeader, CardTitle, CardDescription } from '../components/ui/card.tsx';
import { useToast } from '../context/toastContext.tsx';
import type { ToastContextValue } from '../context/toastContext.tsx';
import { rolesFromAccount } from '../context/authContext.tsx';
import { getMsalInstance } from '../lib/msal.ts';
import { getCertExpiryWarning, hasCertExpiryWarningBeenShown, markCertExpiryWarningShown } from '../lib/certExpiry.ts';

interface PortalConfig {
  devicePreFlightAuthorizationEnabled: boolean;
  sasTokenUrlExpiryMinutes: number;
  bootImageSasExpiryMinutes: number;
  certValidityPeriodDays: number;
  clockSkewToleranceSeconds: number;
  sessionHistoryRetentionDays: number;
}

interface CertMeta {
  thumbprintDisplay?: string;
  issuedAt?: string;
  expiresAt?: string;
  isActive?: boolean;
}

type TabKey = 'certificates' | 'security' | 'preflight' | 'partitioning' | 'misc';

const TABS: { key: TabKey; label: string }[] = [
  { key: 'certificates', label: 'Certificates' },
  { key: 'security',     label: 'Security' },
  { key: 'preflight',    label: 'Preflight' },
  { key: 'partitioning', label: 'Partitioning' },
  { key: 'misc',         label: 'Miscellaneous' },
];

/** True when the two configs have identical persisted field values (no unsaved changes). */
function portalConfigEquals(a: PortalConfig, b: PortalConfig): boolean {
  return a.devicePreFlightAuthorizationEnabled === b.devicePreFlightAuthorizationEnabled
    && a.sasTokenUrlExpiryMinutes === b.sasTokenUrlExpiryMinutes
    && a.bootImageSasExpiryMinutes === b.bootImageSasExpiryMinutes
    && a.certValidityPeriodDays === b.certValidityPeriodDays
    && a.clockSkewToleranceSeconds === b.clockSkewToleranceSeconds
    && a.sessionHistoryRetentionDays === b.sessionHistoryRetentionDays;
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
    sasTokenUrlExpiryMinutes:  240,
    bootImageSasExpiryMinutes: 120,
    certValidityPeriodDays:    365,
    clockSkewToleranceSeconds: 30,
    sessionHistoryRetentionDays: 90,
  };
  const [config, setConfig] = useState<PortalConfig>(DEFAULT_CONFIG);
  /** Snapshot of the config as last loaded/saved. Used to detect unsaved changes. */
  const [savedConfig, setSavedConfig] = useState<PortalConfig>(DEFAULT_CONFIG);
  const isDirty = !portalConfigEquals(config, savedConfig);
  const [certMeta, setCertMeta] = useState<CertMeta | null>(null);
  const [loading, setLoading]   = useState(true);
  const [saveStatus, setSaveStatus] = useState<ButtonStatus>('idle');

  const load = async () => {
    setLoading(true);
    try {
      const [cfgRes, certRes] = await Promise.all([
        apiFetchWithRetry('/api/portal-config', { credentials: 'include' }),
        apiFetchWithRetry('/api/cert/active',   { credentials: 'include' }),
      ]);
      if (cfgRes.ok) {
        const data = await cfgRes.json() as PortalConfig;
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

  const save = async () => {
    setSaveStatus('loading');
    const toastId = notify({ status: 'loading', title: 'Saving configuration…' });
    try {
      const res = await apiFetch('/api/portal-config', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        credentials: 'include',
        body: JSON.stringify(config),
      });
      if (res.ok || res.status === 204) {
        setSavedConfig(config);
        setSaveStatus('success');
        update(toastId, {
          status: 'success',
          title: 'Configuration saved',
          description: 'Deployment settings have been updated.',
        });
        setTimeout(() => setSaveStatus('idle'), 1600);
      } else {
        setSaveStatus('error');
        update(toastId, {
          status: 'error',
          title: 'Could not save configuration',
          description: await extractError(res, 'Failed to save configuration.'),
        });
        setTimeout(() => setSaveStatus('idle'), 1600);
      }
    } catch {
      setSaveStatus('error');
      update(toastId, {
        status: 'error',
        title: 'Could not save configuration',
        description: 'A network error occurred. Please try again.',
      });
      setTimeout(() => setSaveStatus('idle'), 1600);
    }
  };

  if (loading) return <p className="text-muted-foreground">Loading…</p>;

  const numField = (
    key: keyof PortalConfig,
    min: number,
    max: number,
    label: string,
    description: string,
  ) => (
    <div className="space-y-1.5">
      <Label htmlFor={key}>{label}</Label>
      <p className="text-xs text-muted-foreground">{description}</p>
      <Input
        id={key}
        type="number"
        min={min}
        max={max}
        value={config[key] as number}
        onChange={e => setConfig(c => ({ ...c, [key]: Number(e.target.value) }))}
        className="w-40"
      />
    </div>
  );

  return (
    <div className="max-w-2xl space-y-6">
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
              'The lifetime of newly issued boot media certificates before they expire and must be rotated.')}
            {numField('clockSkewToleranceSeconds', 0, 300, 'Clock skew tolerance (seconds)',
              'The permitted time difference between a device clock and the server clock when validating tokens and certificates.')}
          </CardContent>
        </Card>
      )}

      {/* Preflight */}
      {activeTab === 'preflight' && (
        <PreFlightAuthorizationToggle
          enabled={config.devicePreFlightAuthorizationEnabled}
          onChange={v => setConfig(c => ({ ...c, devicePreFlightAuthorizationEnabled: v }))}
        />
      )}

      {/* Partitioning: self-contained, own fetch/save, independent of the Save
          Configuration button below (which only applies to PortalConfig). */}
      {activeTab === 'partitioning' && <PartitioningSchemePanel />}

      {/* Miscellaneous */}
      {activeTab === 'misc' && (
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
              'How long a generated download link for an operating system image stays valid before it must be regenerated.')}
            {numField('bootImageSasExpiryMinutes', 15, 1440, 'Boot image download link expiry (minutes)',
              'How long a generated download link for a boot image stays valid before it must be regenerated.')}
            {numField('sessionHistoryRetentionDays', 1, 3650, 'Session history retention (days)',
              'How long completed session outcomes are kept for reporting before being purged.')}
          </CardContent>
        </Card>
      )}

      {/* Save controls apply to the Security/Preflight/Miscellaneous tabs; the boot
          media certificate panel and the partitioning scheme panel have their own
          separate actions. */}
      {activeTab !== 'certificates' && activeTab !== 'partitioning' && (
        <div className="flex items-center gap-3">
          <Button
            onClick={save}
            status={saveStatus}
            variant={isDirty || saveStatus !== 'idle' ? 'default' : 'secondary'}
            disabled={saveStatus === 'loading' || (!isDirty && saveStatus === 'idle')}
          >
            Save Configuration
          </Button>
          {isDirty && saveStatus === 'idle' && (
            <p className="text-xs text-muted-foreground">You have unsaved changes.</p>
          )}
        </div>
      )}
    </div>
  );
}

