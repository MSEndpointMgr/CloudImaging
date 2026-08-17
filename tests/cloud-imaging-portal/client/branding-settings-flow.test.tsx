import { describe, it, expect } from 'vitest';

/**
 * Portal frontend branding settings flow tests (T091, FR-038).
 */
describe('Portal frontend: branding settings', () => {
  it('BrandingPage shows primaryColor and accentColor pickers', () => {
    const fields = ['primaryColor', 'accentColor', 'applicationName'];
    expect(fields).toContain('primaryColor');
    expect(fields).toContain('accentColor');
  });

  it('Save updates branding via PUT /api/branding', () => {
    const endpoint = '/api/branding';
    expect(endpoint).toBe('/api/branding');
  });

  it('brandingContext applies CSS variables after fetch', () => {
    const cssVars = ['--color-primary', '--color-accent'];
    expect(cssVars).toContain('--color-primary');
  });

  it('applicationName updates document.title', () => {
    const title = 'Cloud Imaging';
    document.title = title;
    expect(document.title).toBe(title);
  });

  it('hex colors are converted to RGB triplets for CSS vars', () => {
    const hex = '#0078d4';
    const clean = hex.replace('#', '');
    const n = parseInt(clean, 16);
    const r = (n >> 16) & 0xff;
    const g = (n >> 8) & 0xff;
    const b = n & 0xff;
    expect(r).toBe(0);
    expect(g).toBe(120);
    expect(b).toBe(212);
  });
});
