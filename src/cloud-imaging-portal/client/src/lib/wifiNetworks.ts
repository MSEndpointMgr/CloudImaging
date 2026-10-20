/**
 * Saved Wi-Fi networks, as the Wi-Fi Networks and Locations pages see them. Passwords are
 * write-only: they go to the server once and no response ever contains one.
 */

export type WifiSecurity = 'Wpa2Personal' | 'Wpa3Personal' | 'Wpa2Wpa3Personal';

export const SECURITY_LABELS: Record<WifiSecurity, string> = {
  Wpa2Personal: 'WPA2-Personal',
  Wpa3Personal: 'WPA3-Personal',
  Wpa2Wpa3Personal: 'WPA2/WPA3-Personal',
};

export const SECURITY_OPTIONS = (Object.keys(SECURITY_LABELS) as WifiSecurity[]).map((value) => ({ value, label: SECURITY_LABELS[value] }));

export interface WifiNetwork {
  networkId: string;
  ssid: string;
  hidden: boolean;
  security: WifiSecurity;
  allLocations: boolean;
  locationIds: string[];
  order: number;
  enabled: boolean;
  passwordChangedAt: string;
  passwordChangedBy?: string | null;
  createdAt: string;
}

/** The editable part of a network. An empty password on edit keeps the saved one. */
export interface WifiNetworkDraft {
  ssid: string;
  hidden: boolean;
  security: WifiSecurity;
  password: string;
  allLocations: boolean;
  locationIds: string[];
  enabled: boolean;
}

export const MAX_WIFI_NETWORKS = 50;

export function emptyDraft(): WifiNetworkDraft {
  return { ssid: '', hidden: false, security: 'Wpa2Personal', password: '', allLocations: true, locationIds: [], enabled: true };
}

export function draftFrom(network: WifiNetwork): WifiNetworkDraft {
  return {
    ssid: network.ssid,
    hidden: network.hidden,
    security: network.security,
    password: '',
    allLocations: network.allLocations,
    locationIds: [...network.locationIds],
    enabled: network.enabled,
  };
}

/** Mirrors WifiNetwork.ValidateSsid in Contracts. */
export function validateSsid(ssid: string, networks: WifiNetwork[], exceptNetworkId?: string): string | null {
  if (!ssid.trim()) return 'Network name is required.';
  if (new TextEncoder().encode(ssid).length > 32) return 'Network name can be at most 32 bytes.';
  return networks.some((n) => n.networkId !== exceptNetworkId && n.ssid === ssid) ? `A network named "${ssid}" already exists.` : null;
}

/** Mirrors WifiNetwork.ValidatePassword in Contracts. */
export function validatePassword(password: string): string | null {
  if (!password) return 'Password is required.';
  if (password.length === 64) return /^[0-9a-fA-F]{64}$/.test(password) ? null : 'A 64-character password must be a hex key.';
  if (password.length < 8 || password.length > 63) return 'Use 8 to 63 characters.';
  return /^[\x20-\x7e]+$/.test(password) ? null : 'Use printable ASCII characters only.';
}

/** True when devices at the location receive the network. */
export function appliesTo(network: Pick<WifiNetwork, 'enabled' | 'allLocations' | 'locationIds'>, locationId: string): boolean {
  return network.enabled && (network.allLocations || network.locationIds.includes(locationId));
}

/** Networks devices at a location receive, in the order they try them. */
export function networksForLocation(networks: WifiNetwork[], locationId: string): WifiNetwork[] {
  return networks.filter((n) => appliesTo(n, locationId)).sort((a, b) => a.order - b.order);
}

/** Locations that stop receiving the network when the draft replaces the saved network. */
export function removedLocationIds(saved: WifiNetwork, draft: WifiNetworkDraft, allLocationIds: string[]): string[] {
  const before = saved.enabled ? (saved.allLocations ? allLocationIds : saved.locationIds) : [];
  const after = new Set(draft.enabled ? (draft.allLocations ? allLocationIds : draft.locationIds) : []);
  return before.filter((id) => !after.has(id));
}

/** Name, Hidden, Security and password are what prepared USB media carries, so only they make it stale. */
export function networkSettingsChanged(saved: WifiNetwork, draft: WifiNetworkDraft): boolean {
  return saved.ssid !== draft.ssid || saved.hidden !== draft.hidden || saved.security !== draft.security || draft.password !== '';
}

export function toPayload(draft: WifiNetworkDraft): Record<string, unknown> {
  return {
    ssid: draft.ssid,
    hidden: draft.hidden,
    security: draft.security,
    ...(draft.password ? { password: draft.password } : {}),
    allLocations: draft.allLocations,
    locationIds: draft.allLocations ? [] : draft.locationIds,
    enabled: draft.enabled,
  };
}

export interface ImportedProfile {
  ssid: string;
  hidden: boolean;
  security: WifiSecurity;
  password: string;
}

/**
 * Reads a Windows WLAN profile exported with `netsh wlan export profile key=clear`. Returns the
 * fields, or an error that says what to change. The file never leaves the browser.
 */
export function parseWlanProfile(xml: string): ImportedProfile | { error: string } {
  const doc = new DOMParser().parseFromString(xml, 'application/xml');
  if (doc.getElementsByTagName('parsererror').length > 0 || doc.documentElement.localName !== 'WLANProfile') {
    return { error: 'This file is not a Windows Wi-Fi profile.' };
  }
  const first = (name: string): string | null => doc.getElementsByTagNameNS('*', name)[0]?.textContent?.trim() ?? null;

  const ssid = doc.getElementsByTagNameNS('*', 'SSID')[0]?.getElementsByTagNameNS('*', 'name')[0]?.textContent ?? first('name');
  if (!ssid) return { error: 'The profile has no network name.' };

  const authentication = first('authentication');
  const transition = first('transitionMode') === 'true';
  let security: WifiSecurity;
  if (authentication === 'WPA2PSK') security = 'Wpa2Personal';
  else if (authentication === 'WPA3SAE') security = transition ? 'Wpa2Wpa3Personal' : 'Wpa3Personal';
  else if (authentication === 'open') return { error: 'Open networks without a password are not supported.' };
  else return { error: 'Enterprise (802.1X) networks cannot be used from boot media.' };

  if (first('protected') === 'true') return { error: 'The password in this file is encrypted. Export it again with key=clear.' };
  const password = first('keyMaterial');
  if (!password) return { error: 'The profile has no password. Export it again with key=clear.' };

  return { ssid, hidden: first('nonBroadcast') === 'true', security, password };
}
