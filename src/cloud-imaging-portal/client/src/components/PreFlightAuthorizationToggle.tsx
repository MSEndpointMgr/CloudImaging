import { Info, ShieldCheck } from 'lucide-react';
import { Card, CardContent, CardHeader, CardTitle, CardDescription } from './ui/card.tsx';
import { Switch } from './ui/switch.tsx';
import { PRE_FLIGHT_ICONS, isPreFlightSettingsValid, type PreFlightCheckName, type PreFlightSettings } from '../lib/preflight.ts';

type RequirementKey = Exclude<keyof PreFlightSettings, 'devicePreFlightAuthorizationEnabled'>;

const REQUIREMENTS: { key: RequirementKey; check: PreFlightCheckName; title: string; description: string }[] = [
  {
    key: 'preFlightRequireAutopilotPresence',
    check: 'AutopilotPresence',
    title: 'Require Autopilot presence',
    description: 'The device must be registered in Windows Autopilot, or imported as a Corporate Identifier.',
  },
  {
    key: 'preFlightRequireUefiFirmware',
    check: 'FirmwareMode',
    title: 'Require UEFI firmware mode',
    description: 'Blocks devices booted in Legacy BIOS (CSM) mode.',
  },
  {
    key: 'preFlightRequireSecureBoot',
    check: 'SecureBoot',
    title: 'Require Secure Boot',
    description: 'Blocks devices where Secure Boot is disabled, in setup mode, or unavailable.',
  },
  {
    key: 'preFlightRequireTpm20',
    check: 'TpmVersion',
    title: 'Require TPM 2.0',
    description: 'Blocks devices whose firmware does not expose a TPM 2.0. A TPM disabled in firmware counts as missing.',
  },
];

/**
 * Device pre-flight authorization: the main switch plus the requirements it enforces. The
 * requirements are hidden while the main switch is off but keep their saved values, so switching
 * it back on restores the previous selection.
 */
export function PreFlightAuthorizationToggle({
  settings, onChange, disabled = false,
}: {
  settings: PreFlightSettings;
  onChange: (next: PreFlightSettings) => void;
  disabled?: boolean;
}): React.ReactElement {
  const enabled = settings.devicePreFlightAuthorizationEnabled;
  return (
    <Card>
      <CardHeader>
        <div className="flex items-center gap-2">
          <ShieldCheck className="h-5 w-5 text-primary" aria-hidden="true" />
          <CardTitle>Device pre-flight authorization</CardTitle>
        </div>
        <CardDescription>
          Checks each device when it starts an imaging session. A device that fails any selected
          requirement is blocked and listed under Devices &rsaquo; Blocked. Disable for open
          bare-metal imaging environments.
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-4">
        <div className="flex items-center justify-between gap-4 rounded-md border border-border bg-muted/30 p-4">
          <div>
            <p className="text-sm font-medium">Require pre-flight authorization</p>
            <p className="mt-0.5 text-sm text-muted-foreground">
              When enabled, a device must meet every selected requirement before it can image.
              Firmware mode, Secure Boot and TPM version are recorded on each session either way.
            </p>
          </div>
          <Switch
            checked={enabled}
            onCheckedChange={v => onChange({ ...settings, devicePreFlightAuthorizationEnabled: v })}
            disabled={disabled}
            label="Require pre-flight authorization"
          />
        </div>

        {enabled && (
          <div className="ml-4 space-y-2 border-l-2 border-primary/50 pl-4">
            {REQUIREMENTS.map(r => {
              const Icon = PRE_FLIGHT_ICONS[r.check];
              return (
                <div key={r.key} className="flex items-center justify-between gap-4 rounded-md border border-border p-4">
                  <div className="flex items-start gap-3">
                    <Icon className="mt-0.5 h-4 w-4 shrink-0 text-muted-foreground" aria-hidden="true" />
                    <div>
                      <p className="text-sm font-medium">{r.title}</p>
                      <p className="mt-0.5 text-sm text-muted-foreground">{r.description}</p>
                    </div>
                  </div>
                  <Switch
                    checked={settings[r.key]}
                    onCheckedChange={v => onChange({ ...settings, [r.key]: v })}
                    disabled={disabled}
                    label={r.title}
                  />
                </div>
              );
            })}
            {!isPreFlightSettingsValid(settings) && (
              <p className="text-sm text-destructive" role="alert">
                Select at least one requirement, or turn pre-flight authorization off.
              </p>
            )}
          </div>
        )}

        <div className="flex items-start gap-2 rounded-md border border-border bg-muted/30 px-3 py-2 text-sm text-muted-foreground">
          <Info className="mt-0.5 h-4 w-4 shrink-0" aria-hidden="true" />
          <p>Changes apply to sessions started after you save. Existing sessions keep the result they were evaluated with.</p>
        </div>
      </CardContent>
    </Card>
  );
}
