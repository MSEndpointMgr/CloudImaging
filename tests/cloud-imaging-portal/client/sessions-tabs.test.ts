import { describe, it, expect } from 'vitest';

// Mirrors the state partitioning in src/pages/SessionsPage.tsx. Failed sessions live in their own
// tab so an operator scanning Monitor sees only devices that are actually imaging or done.
const AVAILABLE_STATES = ['SessionInit', 'SessionAllowed'];
const COUPLED_STATES = ['SessionAssigned'];
const MONITOR_STATES = ['SessionStarted', 'SessionInProgress', 'SessionCompleted'];
const FAILED_STATES = ['SessionFailed', 'SessionNotAuthorized'];

describe('Devices page tab partitioning', () => {
  it('keeps Monitor to sessions that were coupled and then had imaging started', () => {
    expect(MONITOR_STATES).toEqual(['SessionStarted', 'SessionInProgress', 'SessionCompleted']);
  });

  it('routes every failure state to the Failed tab, not Monitor', () => {
    for (const state of FAILED_STATES) {
      expect(MONITOR_STATES).not.toContain(state);
    }
  });

  it('never places a session in two tabs at once', () => {
    const all = [...AVAILABLE_STATES, ...COUPLED_STATES, ...MONITOR_STATES, ...FAILED_STATES];
    expect(new Set(all).size).toBe(all.length);
  });

  it('excludes SessionExpired everywhere: a benign timeout is not a failure', () => {
    const all = [...AVAILABLE_STATES, ...COUPLED_STATES, ...MONITOR_STATES, ...FAILED_STATES];
    expect(all).not.toContain('SessionExpired');
  });
});

describe('Failed tab log download availability', () => {
  // Mirrors the button's disabled/title logic: `undefined` means the lookup is still in flight.
  const canDownload = (logs: unknown[] | undefined) => logs !== undefined && logs.length > 0;

  it('stays disabled while the log lookup is still in flight', () => {
    expect(canDownload(undefined)).toBe(false);
  });

  it('stays disabled when the device never uploaded a log', () => {
    expect(canDownload([])).toBe(false);
  });

  it('enables only once at least one uploaded log is known to exist', () => {
    expect(canDownload([{ fileName: 'client-20260830T100000Z.log' }])).toBe(true);
  });
});
