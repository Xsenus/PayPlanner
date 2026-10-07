import { afterEach, describe, expect, it, vi } from 'vitest';

const ID = 'cefe9b7e3bb64c51b3c80a9e96bb57ea';
const API = 'http://127.0.0.1:5080/api';
afterEach(() => { vi.unstubAllGlobals(); vi.unstubAllEnvs(); vi.resetModules(); });

function isolated() {
  vi.resetModules();
  vi.stubEnv('VITE_RESTORED_RUNTIME_ISOLATION', 'true');
  vi.stubEnv('VITE_API_URL', API);
  vi.stubEnv('VITE_RESTORED_RUNTIME_INSTANCE_ID', ID);
}

describe('restored realtime transports', () => {
  it('leaves ordinary hub URLs and options unchanged', async () => {
    vi.stubEnv('VITE_RESTORED_RUNTIME_ISOLATION', 'false');
    const { restoredRealtimeOptions, restoredResourceUrl } = await import('./restoredRealtime');
    expect(restoredResourceUrl('https://fixture.invalid/hubs/notes')).toBe('https://fixture.invalid/hubs/notes');
    expect(restoredRealtimeOptions(() => 'fixture')).toEqual({});
  });

  it('marks native browser GET transports and verifies before providing the token', async () => {
    isolated();
    const native = vi.fn<typeof fetch>().mockResolvedValue(Response.json({ isolated: true, instanceId: ID }));
    vi.stubGlobal('fetch', native);
    const { restoredRealtimeOptions, restoredResourceUrl } = await import('./restoredRealtime');
    const token = vi.fn(() => 'fixture-token');
    const options = restoredRealtimeOptions(token);
    expect(new URL(restoredResourceUrl('http://127.0.0.1:5080/hubs/notes?fixture=1')).searchParams.get('pp_restored_instance')).toBe(ID);
    expect(options.headers?.['X-PayPlanner-Restored-Instance']).toBe(ID);
    expect(token).not.toHaveBeenCalled();
    expect(await options.accessTokenFactory?.()).toBe('fixture-token');
    expect(token).toHaveBeenCalledTimes(1);
    expect(native).toHaveBeenCalledExactlyOnceWith(`${API}/restored-runtime/identity`, expect.objectContaining({ credentials: 'omit' }));
  });

  it('does not provide a token or negotiate when verification fails', async () => {
    isolated();
    const native = vi.fn<typeof fetch>().mockResolvedValue(new Response('', { status: 404 }));
    vi.stubGlobal('fetch', native);
    const { restoredRealtimeOptions } = await import('./restoredRealtime');
    const token = vi.fn(() => 'fixture-token');
    await expect(restoredRealtimeOptions(token).accessTokenFactory?.()).rejects.toThrow();
    expect(token).not.toHaveBeenCalled();
    expect(native).toHaveBeenCalledTimes(1);
  });

  it('stops both real SignalR connections before negotiation on an ordinary backend', async () => {
    isolated();
    localStorage.setItem('pp_token', 'fixture-token');
    const native = vi.fn<typeof fetch>().mockResolvedValue(new Response('', { status: 404 }));
    vi.stubGlobal('fetch', native);
    const logged = vi.spyOn(console, 'error').mockImplementation(() => undefined);
    const { createNotificationsHubConnection } = await import('./realtimeNotifications');
    const { createNotesHubConnection } = await import('./stickyNotesRealtime');
    const connections = [createNotificationsHubConnection(), createNotesHubConnection()];
    try {
      for (const connection of connections) {
        expect(new URL(connection.baseUrl).searchParams.get('pp_restored_instance')).toBe(ID);
        await expect(connection.start()).rejects.toThrow();
      }
      expect(native).toHaveBeenCalledTimes(1);
      expect(native.mock.calls[0][0]).toBe(`${API}/restored-runtime/identity`);
    } finally {
      await Promise.all(connections.map(connection => connection.stop()));
      logged.mockRestore();
      localStorage.removeItem('pp_token');
    }
  });

  it('rejects resources outside the selected backend origin', async () => {
    isolated();
    const { restoredResourceUrl } = await import('./restoredRealtime');
    expect(() => restoredResourceUrl('https://fixture.invalid/hubs/notes')).toThrow();
    expect(() => restoredResourceUrl('http://127.0.0.1:5081/hubs/notes')).toThrow();
  });

  it('marks local profile photos and does not load copied external avatars', async () => {
    isolated();
    const { restoredPhotoUrl } = await import('./restoredRealtime');
    const photo = new URL(restoredPhotoUrl('/user-photos/fixture.png')!);
    expect(photo.origin).toBe('http://127.0.0.1:5080');
    expect(photo.searchParams.get('pp_restored_instance')).toBe(ID);
    expect(restoredPhotoUrl('https://fixture.invalid/avatar.png')).toBeUndefined();
    expect(restoredPhotoUrl(null)).toBeUndefined();
  });
});
