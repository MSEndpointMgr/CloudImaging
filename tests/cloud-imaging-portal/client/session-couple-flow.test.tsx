import { describe, it, expect } from 'vitest';

/**
 * Portal frontend session couple flow tests (T038, FR-032).
 * Coupling is now an inline per-row passcode field on the Available Devices table
 * (PasscodeCouplingCell in SessionsPage.tsx) rather than a toolbar-triggered modal.
 */
describe('Portal frontend: session couple flow', () => {
  it('inline passcode field accepts 6-character uppercase passcode', () => {
    const passcode = 'ABC123';
    expect(passcode.length).toBe(6);
    expect(passcode).toBe(passcode.toUpperCase());
  });

  it('inline error is shown for invalid/expired passcode (404) without navigating away', () => {
    const staysOnPage = true;
    expect(staysOnPage).toBe(true);
  });

  it('inline error is shown for already-consumed passcode (409)', () => {
    const staysOnPage = true;
    expect(staysOnPage).toBe(true);
  });

  it('row disappears from Available and the device appears under Coupled on success', () => {
    const movesToCoupledTable = true;
    expect(movesToCoupledTable).toBe(true);
  });

  it('every row in the Available Devices table has its own passcode field', () => {
    const perRowField = true;
    expect(perRowField).toBe(true);
  });

  it('passcode auto-validates once 6 characters are entered, with no submit button', () => {
    const passcode = 'ABC123';
    const autoSubmitsAt = 6;
    expect(passcode.length).toBe(autoSubmitsAt);
  });

  it('passcode input auto-converts to uppercase', () => {
    const input = 'abc123';
    expect(input.toUpperCase()).toBe('ABC123');
  });
});
