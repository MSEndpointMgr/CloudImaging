import { describe, it, expect } from 'vitest';
import type { Request, Response } from 'express';
import { requireGuidParams } from '@/middleware/validateParams.js';

/**
 * Route parameter shape validation.
 *
 * Identifiers reaching this backend are always server-generated GUIDs: catalog and session ids
 * in dashed form, staged-upload ids in 32-hex "N" form (Guid.NewGuid().ToString("N")). Anything
 * else is rejected before it can be interpolated into an Operator API request path.
 */
function runGuard(params: Record<string, string>, names: string[]): number {
  let status = 200;
  let nextCalled = false;
  const req = { params } as unknown as Request;
  const res = {
    status(code: number) { status = code; return this; },
    json() { return this; },
  } as unknown as Response;
  requireGuidParams(...names)(req, res, () => { nextCalled = true; });
  return nextCalled ? 200 : status;
}

const DASHED = '3f2504e0-4f89-11d3-9a0c-0305e82c3301';
const HEX32 = '3f2504e04f8911d39a0c0305e82c3301';

describe('Portal backend: route parameter validation', () => {
  it('accepts a dashed GUID', () => {
    expect(runGuard({ sessionId: DASHED }, ['sessionId'])).toBe(200);
  });

  it('accepts an uppercase dashed GUID', () => {
    expect(runGuard({ sessionId: DASHED.toUpperCase() }, ['sessionId'])).toBe(200);
  });

  it('accepts the 32-hex "N" form used for staged upload ids', () => {
    expect(runGuard({ uploadId: HEX32 }, ['uploadId'])).toBe(200);
  });

  it('rejects a path-traversal attempt aimed at another Operator API route', () => {
    expect(runGuard({ sessionId: '../../images' }, ['sessionId'])).toBe(400);
    expect(runGuard({ sessionId: `${DASHED}/../..` }, ['sessionId'])).toBe(400);
  });

  it('rejects an identifier carrying an extra path segment or query', () => {
    expect(runGuard({ imageId: `${DASHED}/sas` }, ['imageId'])).toBe(400);
    expect(runGuard({ imageId: `${DASHED}?x=1` }, ['imageId'])).toBe(400);
  });

  it('rejects malformed, empty and missing identifiers', () => {
    expect(runGuard({ locationId: 'not-a-guid' }, ['locationId'])).toBe(400);
    expect(runGuard({ locationId: '' }, ['locationId'])).toBe(400);
    expect(runGuard({}, ['locationId'])).toBe(400);
  });

  it('rejects when any one of several parameters is invalid', () => {
    expect(runGuard({ sessionId: DASHED, uploadId: 'bad' }, ['sessionId', 'uploadId'])).toBe(400);
  });
});
