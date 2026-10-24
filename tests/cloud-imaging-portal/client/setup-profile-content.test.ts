import { afterEach, describe, expect, it, vi } from 'vitest';

const { apiFetchWithRetry } = vi.hoisted(() => ({ apiFetchWithRetry: vi.fn() }));

vi.mock('../../../src/cloud-imaging-portal/client/src/lib/apiClient.ts', () => ({
  apiFetch: vi.fn(),
  apiFetchWithRetry,
  extractErrorDetail: vi.fn(async (_res: Response, fallback: string) => fallback),
  getApiAccessToken: vi.fn(async () => 'token-1'),
}));

import { SetupProfileApiError, fetchSetupProfileContent, uploadSetupProfileContent } from '../../../src/cloud-imaging-portal/client/src/lib/setupProfiles.ts';

/**
 * Script and answer file texts travel on their own: an upload reports bytes sent and returns the
 * stored reference or the server's reason, and a text is read back for its profile.
 */
class FakeXhr {
  static last: FakeXhr | null = null;
  static respond: (xhr: FakeXhr) => void = () => undefined;
  method = '';
  url = '';
  headers: Record<string, string> = {};
  withCredentials = false;
  status = 0;
  responseText = '';
  body: unknown = null;
  private listeners: Record<string, (() => void)[]> = {};
  upload = { addEventListener: (_type: string, listener: (e: { lengthComputable: boolean; loaded: number; total: number }) => void) => { this.progress = listener; } };
  progress: ((e: { lengthComputable: boolean; loaded: number; total: number }) => void) | null = null;

  constructor() { FakeXhr.last = this; }
  open(method: string, url: string) { this.method = method; this.url = url; }
  setRequestHeader(name: string, value: string) { this.headers[name] = value; }
  addEventListener(type: string, listener: () => void) { (this.listeners[type] ??= []).push(listener); }
  send(body: unknown) { this.body = body; FakeXhr.respond(this); }
  abort() { this.fire('abort'); }
  fire(type: string) { for (const listener of this.listeners[type] ?? []) listener(); }
}

describe('Portal frontend: setup profile texts', () => {
  afterEach(() => { vi.unstubAllGlobals(); vi.clearAllMocks(); });

  it('uploads raw bytes with the token and kind, reports progress and returns the reference', async () => {
    vi.stubGlobal('XMLHttpRequest', FakeXhr);
    FakeXhr.respond = (xhr) => {
      xhr.progress?.({ lengthComputable: true, loaded: 3, total: 6 });
      xhr.status = 200;
      xhr.responseText = JSON.stringify({ sha256: 'a'.repeat(64), sizeBytes: 6 });
      xhr.fire('load');
    };
    const progress = vi.fn();

    const result = await uploadSetupProfileContent('script', 'exit 0', progress);

    expect(result).toEqual({ sha256: 'a'.repeat(64), sizeBytes: 6 });
    expect(FakeXhr.last!.method).toBe('POST');
    expect(FakeXhr.last!.url).toBe('/api/setup-profiles/content?kind=script');
    expect(FakeXhr.last!.headers).toEqual({ Authorization: 'Bearer token-1', 'Content-Type': 'application/octet-stream' });
    expect(FakeXhr.last!.withCredentials).toBe(true);
    expect(FakeXhr.last!.body).toBeInstanceOf(Blob);
    expect(progress).toHaveBeenCalledWith(3, 6);
  });

  it('turns a refusal into its detail, and a network error into the not uploaded message', async () => {
    vi.stubGlobal('XMLHttpRequest', FakeXhr);
    FakeXhr.respond = (xhr) => { xhr.status = 413; xhr.responseText = JSON.stringify({ detail: 'This script is 25.3 MB. A script can be at most 24 MB.' }); xhr.fire('load'); };
    await expect(uploadSetupProfileContent('script', 'x')).rejects.toMatchObject({ message: 'This script is 25.3 MB. A script can be at most 24 MB.', status: 413 });

    FakeXhr.respond = (xhr) => xhr.fire('error');
    const failed = uploadSetupProfileContent('answerFile', 'x');
    await expect(failed).rejects.toBeInstanceOf(SetupProfileApiError);
    await expect(failed).rejects.toMatchObject({ message: 'The answer file was not uploaded.', status: 0 });

    FakeXhr.respond = (xhr) => { xhr.status = 502; xhr.responseText = '<html>'; xhr.fire('load'); };
    await expect(uploadSetupProfileContent('script', 'x')).rejects.toMatchObject({ message: 'The script was not uploaded.' });
  });

  it('stops when aborted', async () => {
    vi.stubGlobal('XMLHttpRequest', FakeXhr);
    FakeXhr.respond = () => undefined;
    const controller = new AbortController();

    const upload = uploadSetupProfileContent('script', 'x', undefined, controller.signal);
    await vi.waitFor(() => expect(FakeXhr.last).not.toBeNull());
    controller.abort();

    await expect(upload).rejects.toMatchObject({ name: 'AbortError' });
    controller.abort();
    await expect(uploadSetupProfileContent('script', 'x', undefined, controller.signal)).rejects.toMatchObject({ name: 'AbortError' });
  });

  it('reads a text for its profile, or refuses with the reason', async () => {
    apiFetchWithRetry.mockResolvedValueOnce(new Response('exit 0', { status: 200 }));
    expect(await fetchSetupProfileContent('p1', 'b'.repeat(64))).toBe('exit 0');
    expect(apiFetchWithRetry).toHaveBeenCalledWith(`/api/setup-profiles/p1/content/${'b'.repeat(64)}`, expect.objectContaining({ credentials: 'include' }));

    apiFetchWithRetry.mockResolvedValueOnce(new Response('', { status: 404 }));
    await expect(fetchSetupProfileContent('p1', 'b'.repeat(64))).rejects.toMatchObject({ status: 404, message: 'Could not load the script.' });
  });
});
