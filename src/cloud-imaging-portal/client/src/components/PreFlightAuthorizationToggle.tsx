import { Fingerprint } from 'lucide-react';
import { Card, CardContent, CardHeader, CardTitle, CardDescription } from './ui/card.tsx';

interface PreFlightAuthorizationToggleProps {
  enabled: boolean;
  onChange: (enabled: boolean) => void;
  disabled?: boolean;
}

/**
 * Toggle control for device pre-flight authorization.
 * When enabled, devices must be in Autopilot or Corporate Identifiers before imaging.
 */
export function PreFlightAuthorizationToggle({
  enabled, onChange, disabled = false,
}: PreFlightAuthorizationToggleProps): React.ReactElement {
  return (
    <Card>
      <CardHeader>
        <div className="flex items-center gap-2">
          <Fingerprint className="h-5 w-5 text-primary" aria-hidden="true" />
          <CardTitle>Device pre-flight authorization</CardTitle>
        </div>
        <CardDescription>
          When enabled, each device must be enrolled in Windows Autopilot or Intune Corporate
          Device Identifiers before an imaging session is allowed to proceed. Disable for open
          bare-metal imaging environments.
        </CardDescription>
      </CardHeader>
      <CardContent className="flex items-center justify-between gap-4 rounded-md border border-border p-4">
        <div>
          <p className="text-sm font-medium">Require pre-flight authorization</p>
          <p className="text-xs text-muted-foreground mt-0.5">
            {enabled ? 'Enabled — unauthorized devices are blocked.' : 'Disabled — any device may image.'}
          </p>
        </div>
        <button
          onClick={() => !disabled && onChange(!enabled)}
          disabled={disabled}
          role="switch"
          aria-checked={enabled}
          className={[
            'relative inline-flex h-6 w-11 shrink-0 items-center rounded-full transition-colors focus:outline-none focus:ring-2 focus:ring-primary focus:ring-offset-2',
            enabled ? 'bg-primary' : 'bg-muted',
            disabled ? 'opacity-50 cursor-not-allowed' : 'cursor-pointer',
          ].join(' ')}
        >
          <span className={[
            'inline-block h-4 w-4 transform rounded-full bg-white shadow transition-transform',
            enabled ? 'translate-x-6' : 'translate-x-1',
          ].join(' ')} />
        </button>
      </CardContent>
    </Card>
  );
}
