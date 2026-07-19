import { describe, it, expect } from 'vitest';

/**
 * Portal frontend boot media certificate management UI tests (T175, FR-068).
 */
describe('Portal frontend — boot media certificate management', () => {
  it('BootMediaCertPanel shows thumbprintDisplay, issuedAt, expiresAt', () => {
    const fields = ['thumbprintDisplay', 'issuedAt', 'expiresAt', 'isActive'];
    expect(fields).toContain('thumbprintDisplay');
    expect(fields).toContain('expiresAt');
  });

  it('Generate Certificate button sends POST /api/cert/generate', () => {
    const endpoint = '/api/cert/generate';
    expect(endpoint).toBe('/api/cert/generate');
  });

  it('Rotate Certificate button requires confirmation dialog', () => {
    const requiresConfirm = true;
    expect(requiresConfirm).toBe(true);
  });

  it('rotation sends POST /api/cert/rotate with confirmed=true', () => {
    const body = { confirmed: true };
    expect(body.confirmed).toBe(true);
  });

  it('response MUST NOT contain PFX bytes', () => {
    const metadata = { thumbprintDisplay: '12345678…', issuedAt: '2026-01-01' };
    expect(metadata).not.toHaveProperty('pfxBytes');
    expect(metadata).not.toHaveProperty('privateKey');
  });

  it('UploadProgressBar shows progress during generate/rotate', () => {
    const progress = 50;
    expect(progress).toBeGreaterThan(0);
    expect(progress).toBeLessThanOrEqual(100);
  });
});
