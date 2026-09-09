import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { parseVersion, compareVersions, isUpdateAvailable } from '../../../src/cloud-imaging-portal/server/src/services/version.js';
import { getUpdateStatus, resetUpdateCheckCache } from '../../../src/cloud-imaging-portal/server/src/services/updateCheck.js';

describe('Portal backend: release version parsing', () => {
  it('parses a release tag with and without the mse-ci-v prefix', () => {
    expect(parseVersion('mse-ci-v1.2.3')).toEqual({ major: 1, minor: 2, patch: 3 });
    expect(parseVersion('1.2.3')).toEqual({ major: 1, minor: 2, patch: 3 });
  });

  it('parses a pre-release suffix', () => {
    expect(parseVersion('mse-ci-v1.2.3-rc.1')).toEqual({ major: 1, minor: 2, patch: 3, preRelease: 'rc.1' });
  });

  it('returns null for values that are not release tags', () => {
    // 'dev' is what a local build stamps; the alias tag never carries a version itself.
    for (const value of ['dev', 'latest', 'mse-ci-iac-latest', '', null, undefined, 'v1.2', 'not-a-version']) {
      expect(parseVersion(value)).toBeNull();
    }
  });

  it('orders 1.10.0 after 1.9.0, which a string comparison would get wrong', () => {
    const nine = parseVersion('mse-ci-v1.9.0')!;
    const ten = parseVersion('mse-ci-v1.10.0')!;
    expect(compareVersions(ten, nine)).toBeGreaterThan(0);
    // Guards the specific regression: lexically, '1.10.0' < '1.9.0'.
    expect('1.10.0' < '1.9.0').toBe(true);
  });

  it('orders a pre-release before the release of the same number', () => {
    const rc = parseVersion('mse-ci-v2.0.0-rc.1')!;
    const release = parseVersion('mse-ci-v2.0.0')!;
    expect(compareVersions(rc, release)).toBeLessThan(0);
  });
});

describe('Portal backend: update availability', () => {
  it('reports an update when latest is genuinely newer', () => {
    expect(isUpdateAvailable('mse-ci-v1.2.0', 'mse-ci-v1.3.0')).toBe(true);
    expect(isUpdateAvailable('mse-ci-v1.9.0', 'mse-ci-v1.10.0')).toBe(true);
  });

  it('reports no update when the versions match or latest is older', () => {
    expect(isUpdateAvailable('mse-ci-v1.3.0', 'mse-ci-v1.3.0')).toBe(false);
    expect(isUpdateAvailable('mse-ci-v1.3.0', 'mse-ci-v1.2.0')).toBe(false);
  });

  it('never prompts an upgrade to a pre-release', () => {
    // The alias only ever moves on stable releases, so a pre-release here means something
    // unexpected happened and is not worth prompting over.
    expect(isUpdateAvailable('mse-ci-v1.2.0', 'mse-ci-v1.3.0-rc.1')).toBe(false);
  });

  it('reports no update when the current version is unknown, e.g. a local build', () => {
    expect(isUpdateAvailable('dev', 'mse-ci-v1.3.0')).toBe(false);
    expect(isUpdateAvailable(null, 'mse-ci-v1.3.0')).toBe(false);
  });
});

describe('Portal backend: update check service', () => {
  const fetchMock = vi.fn();

  beforeEach(() => {
    resetUpdateCheckCache();
    fetchMock.mockReset();
    vi.stubGlobal('fetch', fetchMock);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('makes NO outbound request while the check is disabled', async () => {
    const result = await getUpdateStatus(false);
    // The whole point of the opt-in is that no traffic leaves the tenant, so this has to be
    // enforced server-side rather than merely hidden in the interface.
    expect(fetchMock).not.toHaveBeenCalled();
    expect(result.status).toBe('disabled');
    expect(result.latest).toBeNull();
    expect(result.updateAvailable).toBe(false);
  });

  it('resolves the version from the alias release title', async () => {
    fetchMock.mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => ({
        tag_name: 'mse-ci-iac-latest',
        name: 'Cloud Imaging (latest — mse-ci-v9.9.9)',
        html_url: 'https://github.com/MSEndpointMgr/CloudImaging/releases/tag/mse-ci-iac-latest',
      }),
    });

    const result = await getUpdateStatus(true);
    expect(result.status).toBe('ok');
    expect(result.latest).toBe('mse-ci-v9.9.9');
  });

  it('queries the iac-latest alias, not GitHub repo-wide latest', async () => {
    fetchMock.mockResolvedValue({ ok: true, status: 200, json: async () => ({ name: 'mse-ci-v1.0.0' }) });
    await getUpdateStatus(true);
    // Repo-wide /releases/latest would resolve to whichever of the three streams published
    // most recently, and could advertise a Client release as a portal upgrade.
    const [url] = fetchMock.mock.calls[0] as [string];
    expect(url).toContain('/releases/tags/mse-ci-iac-latest');
    expect(url).not.toMatch(/\/releases\/latest$/);
  });

  it('caches the lookup so repeated requests do not spend GitHub rate limit', async () => {
    fetchMock.mockResolvedValue({ ok: true, status: 200, json: async () => ({ name: 'mse-ci-v1.0.0' }) });
    await getUpdateStatus(true);
    await getUpdateStatus(true);
    await getUpdateStatus(true);
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('degrades to unreachable when there is no outbound access', async () => {
    fetchMock.mockRejectedValue(new Error('getaddrinfo ENOTFOUND api.github.com'));
    const result = await getUpdateStatus(true);
    expect(result.status).toBe('unreachable');
    expect(result.updateAvailable).toBe(false);
  });

  it('degrades to rate-limited on HTTP 403', async () => {
    fetchMock.mockResolvedValue({ ok: false, status: 403, json: async () => ({}) });
    const result = await getUpdateStatus(true);
    expect(result.status).toBe('rate-limited');
    expect(result.updateAvailable).toBe(false);
  });

  it('keeps serving a cached version when a refresh is rate-limited, but reports it honestly', async () => {
    vi.useFakeTimers();
    try {
      fetchMock.mockResolvedValue({
        ok: true,
        status: 200,
        json: async () => ({ name: 'mse-ci-v9.9.9', html_url: 'https://example.invalid/release' }),
      });
      const first = await getUpdateStatus(true);
      expect(first.status).toBe('ok');

      // Expire the cache, then have the refresh come back rate-limited.
      vi.advanceTimersByTime(7 * 60 * 60 * 1000);
      fetchMock.mockResolvedValue({ ok: false, status: 403, json: async () => ({}) });
      const second = await getUpdateStatus(true);

      // The known-good version is still served, so the banner doesn't flap off and back on.
      expect(second.latest).toBe('mse-ci-v9.9.9');
      // But the status must not claim 'ok' for a call that failed, and checkedAt must still be
      // when the data was actually obtained rather than when the failed attempt happened.
      expect(second.status).toBe('rate-limited');
      expect(second.checkedAt).toBe(first.checkedAt);
    } finally {
      vi.useRealTimers();
    }
  });

  it('degrades to unknown when the alias release does not exist', async () => {
    fetchMock.mockResolvedValue({ ok: false, status: 404, json: async () => ({}) });
    const result = await getUpdateStatus(true);
    expect(result.status).toBe('unknown');
    expect(result.latest).toBeNull();
  });

  it('never throws, whatever the upstream does', async () => {
    fetchMock.mockResolvedValue({ ok: true, status: 200, json: async () => { throw new Error('malformed body'); } });
    await expect(getUpdateStatus(true)).resolves.toMatchObject({ status: 'unreachable' });
  });
});
