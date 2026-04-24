/**
 * Tests for ProjectRegistry:
 *   - UDP message parsing (handleUdpMessage)
 *   - State transitions driven by applyPollOutcome
 *
 * We stub fetch and inject a controllable `now()` so all transitions can be
 * asserted deterministically.
 */
import { describe, test, expect, beforeEach, jest } from '@jest/globals';
import { ProjectRegistry } from '../core/ProjectRegistry.js';
import { UnityConnection } from '../core/UnityConnection.js';

function makeRegistry(opts: {
    now: () => number;
    cooldownMs?: number;
    staleMs?: number;
}): { registry: ProjectRegistry; conn: UnityConnection } {
    UnityConnection.resetInstanceForTesting();
    const conn = UnityConnection.getInstance();
    const registry = new ProjectRegistry(conn, {
        nowImpl: opts.now,
        unhealthyCooldownMs: opts.cooldownMs ?? 60_000,
        staleThresholdMs: opts.staleMs ?? 90_000,
        initialHealthFetchEnabled: false,
    });
    return { registry, conn };
}

describe('ProjectRegistry.handleUdpMessage', () => {
    beforeEach(() => {
        UnityConnection.resetInstanceForTesting();
    });

    test('parses a valid unity_announce and registers instance', () => {
        let t = 1_000_000;
        const { registry, conn } = makeRegistry({ now: () => t });

        const msg = Buffer.from(JSON.stringify({
            type: 'unity_announce',
            n: 'MyGame',
            port: 27182,
            path: '/some/path',
            unity: '2022.3.10f1',
            v: '2.0.0',
        }));
        const rinfo = { address: '127.0.0.1', port: 12345, family: 'IPv4', size: msg.length } as any;

        const result = registry.handleUdpMessage(msg, rinfo);

        expect(result).not.toBeNull();
        expect(result!.id).toBe('MyGame-27182');
        expect(result!.projectName).toBe('MyGame');
        expect(result!.port).toBe(27182);
        expect(result!.endpoint).toBe('http://127.0.0.1:27182');
        expect(result!.state).toBe('healthy');
        expect(result!.lastSeen).toBe(1_000_000);
        expect(conn.getAllInstances()).toHaveLength(1);
    });

    test('ignores non-announce UDP messages', () => {
        const { registry, conn } = makeRegistry({ now: () => 0 });
        const msg = Buffer.from(JSON.stringify({ type: 'something_else' }));
        const rinfo = { address: '127.0.0.1', port: 1 } as any;

        const result = registry.handleUdpMessage(msg, rinfo);
        expect(result).toBeNull();
        expect(conn.getAllInstances()).toHaveLength(0);
    });

    test('ignores announce without port', () => {
        const { registry, conn } = makeRegistry({ now: () => 0 });
        const msg = Buffer.from(JSON.stringify({ type: 'unity_announce', n: 'X' }));
        const rinfo = { address: '127.0.0.1', port: 1 } as any;

        const result = registry.handleUdpMessage(msg, rinfo);
        expect(result).toBeNull();
        expect(conn.getAllInstances()).toHaveLength(0);
    });

    test('handles malformed JSON gracefully', () => {
        const { registry, conn } = makeRegistry({ now: () => 0 });
        const msg = Buffer.from('not json{{{');
        const rinfo = { address: '127.0.0.1', port: 1 } as any;

        const result = registry.handleUdpMessage(msg, rinfo);
        expect(result).toBeNull();
        expect(conn.getAllInstances()).toHaveLength(0);
    });

    test('0.0.0.0 is rewritten to 127.0.0.1 in endpoint', () => {
        const { registry } = makeRegistry({ now: () => 0 });
        const msg = Buffer.from(JSON.stringify({
            type: 'unity_announce',
            n: 'Proj',
            port: 27185,
        }));
        const rinfo = { address: '0.0.0.0', port: 1 } as any;
        const r = registry.handleUdpMessage(msg, rinfo);
        expect(r!.endpoint).toBe('http://127.0.0.1:27185');
    });
});

describe('ProjectRegistry.applyPollOutcome (state machine)', () => {
    beforeEach(() => {
        UnityConnection.resetInstanceForTesting();
    });

    function seed(now: number, conn: UnityConnection, id: string = 'P-1'): void {
        conn.registerInstance({
            id,
            projectName: 'P',
            projectPath: '',
            port: 1,
            unityVersion: '',
            endpoint: 'http://127.0.0.1:1',
            version: '',
            state: 'healthy',
            lastSeen: now,
            lastContact: now,
            consecutiveFailures: 0,
        });
    }

    test('200 ok keeps healthy, resets consecutiveFailures', () => {
        let t = 1000;
        const { registry, conn } = makeRegistry({ now: () => t });
        seed(t, conn);
        t = 2000;
        registry.applyPollOutcome('P-1', true);
        const inst = conn.getInstanceById('P-1')!;
        expect(inst.state).toBe('healthy');
        expect(inst.lastSeen).toBe(2000);
        expect(inst.lastContact).toBe(2000);
        expect(inst.consecutiveFailures).toBe(0);
    });

    test('failure from healthy → reloading, failures=1', () => {
        let t = 1000;
        const { registry, conn } = makeRegistry({ now: () => t });
        seed(t, conn);
        t = 2000;
        registry.applyPollOutcome('P-1', false);
        const inst = conn.getInstanceById('P-1')!;
        expect(inst.state).toBe('reloading');
        expect(inst.consecutiveFailures).toBe(1);
        // lastContact should NOT have been bumped on failure.
        expect(inst.lastContact).toBe(1000);
    });

    test('failure from reloading within cooldown stays reloading', () => {
        let t = 1000;
        const { registry, conn } = makeRegistry({ now: () => t, cooldownMs: 60_000 });
        seed(t, conn);
        // First failure: healthy → reloading
        t = 2000; registry.applyPollOutcome('P-1', false);
        // Second failure but still within cooldown (60s) — stays reloading
        t = 30_000; registry.applyPollOutcome('P-1', false);
        const inst = conn.getInstanceById('P-1')!;
        expect(inst.state).toBe('reloading');
        expect(inst.consecutiveFailures).toBe(2);
    });

    test('failure from reloading with >=2 failures and past cooldown → unhealthy', () => {
        let t = 1000;
        const { registry, conn } = makeRegistry({ now: () => t, cooldownMs: 60_000 });
        seed(t, conn);
        // 1st failure → reloading
        t = 2000; registry.applyPollOutcome('P-1', false);
        // 2nd failure, now > cooldown from lastContact (1000 + 60000 = 61000)
        t = 70_000; registry.applyPollOutcome('P-1', false);
        const inst = conn.getInstanceById('P-1')!;
        expect(inst.state).toBe('unhealthy');
    });

    test('unhealthy stays unhealthy on failure', () => {
        let t = 1000;
        const { registry, conn } = makeRegistry({ now: () => t, cooldownMs: 100 });
        seed(t, conn);
        t = 2000; registry.applyPollOutcome('P-1', false);  // → reloading
        t = 3000; registry.applyPollOutcome('P-1', false);  // → unhealthy (>=2 fails, >cooldown)
        t = 4000; registry.applyPollOutcome('P-1', false);  // stays unhealthy
        const inst = conn.getInstanceById('P-1')!;
        expect(inst.state).toBe('unhealthy');
    });

    test('200 ok recovers unhealthy → healthy', () => {
        let t = 1000;
        const { registry, conn } = makeRegistry({ now: () => t, cooldownMs: 100 });
        seed(t, conn);
        t = 2000; registry.applyPollOutcome('P-1', false);
        t = 3000; registry.applyPollOutcome('P-1', false);
        expect(conn.getInstanceById('P-1')!.state).toBe('unhealthy');
        t = 4000; registry.applyPollOutcome('P-1', true);
        expect(conn.getInstanceById('P-1')!.state).toBe('healthy');
        expect(conn.getInstanceById('P-1')!.consecutiveFailures).toBe(0);
    });

    test('UDP announce resets state → healthy from any prior state', () => {
        let t = 1000;
        const { registry, conn } = makeRegistry({ now: () => t, cooldownMs: 100 });
        seed(t, conn);
        t = 2000; registry.applyPollOutcome('P-1', false);
        t = 3000; registry.applyPollOutcome('P-1', false);
        expect(conn.getInstanceById('P-1')!.state).toBe('unhealthy');

        t = 10_000;
        const msg = Buffer.from(JSON.stringify({
            type: 'unity_announce', n: 'P', port: 1,
        }));
        const rinfo = { address: '127.0.0.1', port: 1 } as any;
        registry.handleUdpMessage(msg, rinfo);

        const inst = conn.getInstanceById('P-1')!;
        expect(inst.state).toBe('healthy');
        expect(inst.lastSeen).toBe(10_000);
        expect(inst.consecutiveFailures).toBe(0);
    });

    test('sweepStaleUnhealthy removes unhealthy instances past staleThreshold', () => {
        let t = 1000;
        const { registry, conn } = makeRegistry({
            now: () => t, cooldownMs: 100, staleMs: 5_000,
        });
        seed(t, conn);
        t = 2000; registry.applyPollOutcome('P-1', false);
        t = 3000; registry.applyPollOutcome('P-1', false);
        expect(conn.getInstanceById('P-1')!.state).toBe('unhealthy');
        // Still within stale window.
        t = 4000; registry.sweepStaleUnhealthy();
        expect(conn.getInstanceById('P-1')).toBeDefined();

        // lastSeen was 1000. 1000 + 5000 = 6000 threshold.
        t = 10_000; registry.sweepStaleUnhealthy();
        expect(conn.getInstanceById('P-1')).toBeUndefined();
    });
});
