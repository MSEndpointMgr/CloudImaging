interface PreFlightAuthorizationToggleProps {
  enabled: boolean;
  onChange: (enabled: boolean) => void;
  disabled?: boolean;
}

/**
 * Toggle control for device pre-flight authorization (T147, FR-026).
 * When enabled, devices must be in Autopilot or Corporate Identifiers before imaging.
 */
export function PreFlightAuthorizationToggle({
  enabled, onChange, disabled = false,
}: PreFlightAuthorizationToggleProps): React.ReactElement {
  return (
    <div className="flex items-start justify-between gap-4 rounded-md border border-border p-4">
      <div className="flex-1">
        <h4 className="text-sm font-semibold">Device Pre-Flight Authorization</h4>
        <p className="text-xs text-muted-foreground mt-1">
          When enabled, each device must be enrolled in Windows Autopilot or Intune Corporate
          Device Identifiers before an imaging session is allowed to proceed. Disable for open
          bare-metal imaging environments.
        </p>
      </div>
      <button
        onClick={() => !disabled && onChange(!enabled)}
        disabled={disabled}
        role="switch"
        aria-checked={enabled}
        className={[
          'relative inline-flex h-6 w-11 items-center rounded-full transition-colors focus:outline-none focus:ring-2 focus:ring-primary focus:ring-offset-2',
          enabled ? 'bg-primary' : 'bg-muted',
          disabled ? 'opacity-50 cursor-not-allowed' : 'cursor-pointer',
        ].join(' ')}
      >
        <span className={[
          'inline-block h-4 w-4 transform rounded-full bg-white shadow transition-transform',
          enabled ? 'translate-x-6' : 'translate-x-1',
        ].join(' ')} />
      </button>
    </div>
  );
}
