import { Fingerprint } from 'lucide-react';
import { Card, CardContent, CardHeader, CardTitle, CardDescription } from './ui/card.tsx';
import { Switch } from './ui/switch.tsx';

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
      <CardContent>
        <div className="flex items-center justify-between gap-4 rounded-md border border-border bg-muted/30 p-4">
          <div>
            <p className="text-sm font-medium">Require pre-flight authorization</p>
            <p className="text-sm text-muted-foreground mt-0.5">
              {enabled ? 'Enabled. Unauthorized devices are blocked.' : 'Disabled. Any device may image.'}
            </p>
          </div>
          <Switch
            checked={enabled}
            onCheckedChange={onChange}
            disabled={disabled}
            label="Require pre-flight authorization"
          />
        </div>
      </CardContent>
    </Card>
  );
}
