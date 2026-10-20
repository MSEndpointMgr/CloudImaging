import { describe, it, expect } from 'vitest';
import {
  appliesTo,
  networkSettingsChanged,
  networksForLocation,
  parseWlanProfile,
  removedLocationIds,
  toPayload,
  validatePassword,
  validateSsid,
  type WifiNetwork,
} from '../../../src/cloud-imaging-portal/client/src/lib/wifiNetworks.ts';

/**
 * Managed Wi-Fi networks: the portal must validate like the server, warn about exactly the
 * changes that take connectivity away or make USB media stale, never send an unchanged password,
 * and read netsh profile exports without the file leaving the browser.
 */

const network = (overrides: Partial<WifiNetwork> = {}): WifiNetwork => ({
  networkId: 'n1',
  ssid: 'STO-Deploy',
  hidden: false,
  security: 'Wpa2Personal',
  allLocations: false,
  locationIds: ['sto', 'got', 'mal'],
  order: 1,
  enabled: true,
  passwordChangedAt: '2026-09-03T14:12:00Z',
  createdAt: '2026-09-01T00:00:00Z',
  ...overrides,
});

const draftOf = (n: WifiNetwork) => ({ ssid: n.ssid, hidden: n.hidden, security: n.security, password: '', allLocations: n.allLocations, locationIds: [...n.locationIds], enabled: n.enabled });

describe('validation', () => {
  it('mirrors the server rules for names and passwords', () => {
    expect(validateSsid('', [])).toBe('Network name is required.');
    expect(validateSsid('\u00e5'.repeat(17), [])).toBe('Network name can be at most 32 bytes.');
    expect(validateSsid('STO-Deploy', [network()])).toBe('A network named "STO-Deploy" already exists.');
    expect(validateSsid('STO-Deploy', [network()], 'n1')).toBeNull();
    expect(validatePassword('short')).toBe('Use 8 to 63 characters.');
    expect(validatePassword('a'.repeat(64))).toBeNull();
    expect(validatePassword('z'.repeat(64))).toBe('A 64-character password must be a hex key.');
    expect(validatePassword('l\u00f6senord123')).toBe('Use printable ASCII characters only.');
  });
});

describe('which locations receive a network', () => {
  it('counts all-locations networks for every location and skips disabled ones, in order', () => {
    const everywhere = network({ networkId: 'n0', ssid: 'Everywhere', allLocations: true, locationIds: [], order: 2 });
    const off = network({ networkId: 'n2', ssid: 'Off', enabled: false, order: 3 });
    expect(networksForLocation([off, everywhere, network()], 'sto').map((n) => n.ssid)).toEqual(['STO-Deploy', 'Everywhere']);
    expect(appliesTo(network(), 'kista')).toBe(false);
  });
});

describe('edit warnings', () => {
  const all = ['sto', 'got', 'mal', 'upp'];

  it('reports removed locations, including switching off or narrowing from all locations', () => {
    const saved = network();
    expect(removedLocationIds(saved, { ...draftOf(saved), locationIds: ['sto'] }, all)).toEqual(['got', 'mal']);
    expect(removedLocationIds(saved, { ...draftOf(saved), locationIds: [...saved.locationIds, 'upp'] }, all)).toEqual([]);
    expect(removedLocationIds(saved, { ...draftOf(saved), enabled: false }, all)).toEqual(['sto', 'got', 'mal']);
    const everywhere = network({ allLocations: true, locationIds: [] });
    expect(removedLocationIds(everywhere, { ...draftOf(everywhere), allLocations: false, locationIds: ['sto'] }, all)).toEqual(['got', 'mal', 'upp']);
  });

  it('flags stale USB media only for name, hidden, security or a new password', () => {
    const saved = network();
    expect(networkSettingsChanged(saved, { ...draftOf(saved), locationIds: ['sto'], enabled: false })).toBe(false);
    expect(networkSettingsChanged(saved, { ...draftOf(saved), hidden: true })).toBe(true);
    expect(networkSettingsChanged(saved, { ...draftOf(saved), password: 'new password' })).toBe(true);
  });

  it('leaves an unchanged password out of the request', () => {
    expect(toPayload(draftOf(network()))).not.toHaveProperty('password');
    expect(toPayload({ ...draftOf(network()), allLocations: true })).toMatchObject({ allLocations: true, locationIds: [] });
  });
});

describe('parseWlanProfile', () => {
  const profile = (auth: string, extra = '', key = '<keyMaterial>correct horse</keyMaterial>', protectedKey = 'false') => `<?xml version="1.0"?>
<WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1">
  <name>Contoso-Imaging</name>
  <SSIDConfig><SSID><hex>436F6E746F736F</hex><name>Contoso-Imaging</name></SSID><nonBroadcast>true</nonBroadcast></SSIDConfig>
  <MSM><security>
    <authEncryption><authentication>${auth}</authentication><encryption>AES</encryption><useOneX>false</useOneX>${extra}</authEncryption>
    <sharedKey><keyType>passPhrase</keyType><protected>${protectedKey}</protected>${key}</sharedKey>
  </security></MSM>
</WLANProfile>`;

  it('reads a WPA2 export with the clear key and the hidden flag', () => {
    expect(parseWlanProfile(profile('WPA2PSK'))).toEqual({ ssid: 'Contoso-Imaging', hidden: true, security: 'Wpa2Personal', password: 'correct horse' });
  });

  it('maps WPA3 and the WPA2/WPA3 transition mode', () => {
    expect(parseWlanProfile(profile('WPA3SAE'))).toMatchObject({ security: 'Wpa3Personal' });
    expect(parseWlanProfile(profile('WPA3SAE', '<transitionMode xmlns="http://www.microsoft.com/networking/WLAN/profile/v4">true</transitionMode>'))).toMatchObject({ security: 'Wpa2Wpa3Personal' });
  });

  it('explains what to change for unsupported or encrypted profiles', () => {
    expect(parseWlanProfile(profile('WPA2'))).toEqual({ error: 'Enterprise (802.1X) networks cannot be used from boot media.' });
    expect(parseWlanProfile(profile('WPA2PSK', '', '<keyMaterial>01000000D08C9DDF</keyMaterial>', 'true'))).toEqual({ error: 'The password in this file is encrypted. Export it again with key=clear.' });
    expect(parseWlanProfile('<html></html>')).toEqual({ error: 'This file is not a Windows Wi-Fi profile.' });
  });
});
