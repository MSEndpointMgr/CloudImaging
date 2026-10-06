import { describe, it, expect, afterEach } from 'vitest';
import { render, screen, cleanup, fireEvent } from '@testing-library/react';
import {
  blockedSessions,
  formatExpiresIn,
  isPreFlightSettingsValid,
  orderedChecks,
  overrideChecks,
  preFlightChipLabel,
  preFlightFixHint,
  preFlightValue,
  type PreFlightOverride,
  type PreFlightSettings,
} from '../../../src/cloud-imaging-portal/client/src/lib/preflight.ts';
import { PreFlightCheckStrip } from '../../../src/cloud-imaging-portal/client/src/components/PreFlightChecks.tsx';
import { PreFlightAuthorizationToggle } from '../../../src/cloud-imaging-portal/client/src/components/PreFlightAuthorizationToggle.tsx';

const override: PreFlightOverride = {
  serialNumber: 'PF4K7T2M',
  coveredChecks: ['FirmwareMode', 'SecureBoot'],
  sourceSessionId: '3f2a9c1e-7b4d-4e2a-9c51-8d0e6b2f4a17',
  deviceManufacturer: 'Dell Inc.',
  deviceModel: 'OptiPlex 7010',
  locationName: null,
  sourceChecks: [
    { check: 'AutopilotPresence', outcome: 'Passed', observed: 'Autopilot' },
    { check: 'FirmwareMode', outcome: 'Failed', observed: 'LegacyBios' },
    { check: 'SecureBoot', outcome: 'Failed', observed: 'Disabled' },
    { check: 'TpmVersion', outcome: 'NotRequired', observed: 'Tpm20' },
  ],
  approvedBy: 'j.doe@contoso.com',
  approvedAt: '2026-10-06T09:00:00Z',
  expiresAt: '2026-10-13T09:00:00Z',
};

const settings: PreFlightSettings = {
  devicePreFlightAuthorizationEnabled: true,
  preFlightRequireAutopilotPresence: false,
  preFlightRequireUefiFirmware: false,
  preFlightRequireSecureBoot: false,
  preFlightRequireTpm20: false,
};

describe('Pre-flight check text', () => {
  it('uses the same wording as the device', () => {
    expect(preFlightValue('Unsupported')).toBe('Not enabled');
    expect(preFlightValue('Tpm20')).toBe('Version 2.0 or above');
    expect(preFlightValue('Unknown')).toBe('Could not be detected');
    expect(preFlightFixHint('FirmwareMode', 'LegacyBios')).toBe('Switch to UEFI in firmware settings.');
    expect(preFlightFixHint('SecureBoot', 'NotReported')).toBe('Update the boot media.');
  });

  it('labels chips with name and value, and marks approvals', () => {
    expect(preFlightChipLabel({ check: 'SecureBoot', outcome: 'Failed', observed: 'Disabled' })).toBe('Secure Boot: Not enabled');
    expect(preFlightChipLabel({ check: 'TpmVersion', outcome: 'NotRequired', observed: 'Tpm20' })).toBe('TPM version: Not required');
    expect(preFlightChipLabel({ check: 'FirmwareMode', outcome: 'Approved', observed: 'LegacyBios' })).toBe('Firmware mode: Legacy BIOS (CSM) (approved)');
  });

  it('orders checks the same way everywhere', () => {
    const ordered = orderedChecks([...override.sourceChecks].reverse()).map(c => c.check);
    expect(ordered).toEqual(['AutopilotPresence', 'FirmwareMode', 'SecureBoot', 'TpmVersion']);
  });
});

describe('Pre-flight overrides', () => {
  it('shows only the covered checks as approved', () => {
    const outcomes = overrideChecks(override).map(c => c.outcome);
    expect(outcomes).toEqual(['Passed', 'Approved', 'Approved', 'NotRequired']);
  });

  it('drops an approved session from Blocked', () => {
    const sessions = [
      { sessionId: override.sourceSessionId, state: 'SessionNotAuthorized' },
      { sessionId: 'b1', state: 'SessionNotAuthorized' },
      { sessionId: 'f1', state: 'SessionFailed' },
    ];
    expect(blockedSessions(sessions, [override]).map(s => s.sessionId)).toEqual(['b1']);
    expect(blockedSessions(sessions, []).map(s => s.sessionId)).toEqual([override.sourceSessionId, 'b1']);
  });

  it('formats the time left before an override expires', () => {
    const now = Date.parse('2026-10-06T09:00:00Z');
    expect(formatExpiresIn('2026-10-13T09:00:00Z', now)).toBe('in 7 days');
    expect(formatExpiresIn('2026-10-07T10:00:00Z', now)).toBe('in 1 day');
    expect(formatExpiresIn('2026-10-06T14:30:00Z', now)).toBe('in 5 hr');
    expect(formatExpiresIn('2026-10-06T09:20:00Z', now)).toBe('in 20 min');
  });
});

describe('Pre-flight check strip', () => {
  afterEach(cleanup);

  it('exposes every chip by name and value', () => {
    render(<PreFlightCheckStrip checks={overrideChecks(override)} />);
    expect(screen.getByRole('img', { name: 'Autopilot presence: Present in Autopilot' })).toBeTruthy();
    expect(screen.getByRole('img', { name: 'Secure Boot: Not enabled (approved)' })).toBeTruthy();
    expect(screen.getByRole('img', { name: 'TPM version: Not required' })).toBeTruthy();
  });
});

describe('Pre-flight configuration card', () => {
  afterEach(cleanup);

  it('requires at least one requirement while switched on', () => {
    expect(isPreFlightSettingsValid(settings)).toBe(false);
    expect(isPreFlightSettingsValid({ ...settings, preFlightRequireSecureBoot: true })).toBe(true);
    expect(isPreFlightSettingsValid({ ...settings, devicePreFlightAuthorizationEnabled: false })).toBe(true);
  });

  it('hides the requirements while the main switch is off', () => {
    render(<PreFlightAuthorizationToggle settings={{ ...settings, devicePreFlightAuthorizationEnabled: false }} onChange={() => {}} />);
    expect(screen.queryByRole('switch', { name: 'Require Secure Boot' })).toBeNull();
  });

  it('toggles a single requirement and keeps the rest', () => {
    let next: PreFlightSettings | null = null;
    render(<PreFlightAuthorizationToggle settings={settings} onChange={s => { next = s; }} />);
    expect(screen.getByRole('alert').textContent).toContain('Select at least one requirement');

    fireEvent.click(screen.getByRole('switch', { name: 'Require TPM 2.0' }));
    expect(next).toEqual({ ...settings, preFlightRequireTpm20: true });
  });
});
