/**
 * Tests for CodeExecutorHandler (built-in MCP tool `unity_execute_code`).
 *
 * Coverage:
 *   - commandPrefix / tool-name registration
 *   - Idempotency classification (unsafe)
 *   - execute() POSTs to `/execute_code` (not `/command`) with a flat {code} body
 *   - Envelope unwrap: `{status:"success",result:{...}}` → returns result verbatim
 *   - `target` parameter is forwarded to sendToEndpoint opts (not leaked into body)
 */
import { describe, test, expect, beforeEach, afterEach } from '@jest/globals';
import * as http from 'http';
import * as net from 'net';
import { AddressInfo } from 'net';
import { CodeExecutorHandler } from '../handlers/CodeExecutorHandler.js';
import { UnityConnection, UnityInstance } from '../core/UnityConnection.js';

async function freePort(): Promise<number> {
    return new Promise((resolve, reject) => {
        const srv = net.createServer();
        srv.unref();
        srv.on('error', reject);
        srv.listen(0, '127.0.0.1', () => {
            const port = (srv.address() as AddressInfo).port;
            srv.close(() => resolve(port));
        });
    });
}

function seedInstance(conn: UnityConnection, endpoint: string, id = 'MyGame-27182', projectName = 'MyGame'): UnityInstance {
    const inst: UnityInstance = {
        id,
        projectName,
        projectPath: '',
        port: 0,
        unityVersion: '2022.3',
        endpoint,
        version: '2.1.0',
        state: 'healthy',
        lastSeen: Date.now(),
        lastContact: Date.now(),
        consecutiveFailures: 0,
    };
    conn.registerInstance(inst);
    return inst;
}

interface RecordedRequest {
    method: string;
    url: string;
    body: string;
    contentType?: string;
}

function startStubServer(
    responder: (req: RecordedRequest) => { status: number; body: string }
): Promise<{ server: http.Server; port: number; requests: RecordedRequest[] }> {
    const requests: RecordedRequest[] = [];
    return new Promise((resolve, reject) => {
        const server = http.createServer((req, res) => {
            const chunks: Buffer[] = [];
            req.on('data', (c) => chunks.push(c));
            req.on('end', () => {
                const rec: RecordedRequest = {
                    method: req.method ?? '',
                    url: req.url ?? '',
                    body: Buffer.concat(chunks).toString('utf8'),
                    contentType: req.headers['content-type'] as string | undefined,
                };
                requests.push(rec);
                const resp = responder(rec);
                res.statusCode = resp.status;
                res.setHeader('content-type', 'application/json');
                res.end(resp.body);
            });
            req.on('error', () => { /* swallow */ });
        });
        server.on('error', reject);
        freePort().then((port) => {
            server.listen(port, '127.0.0.1', () => {
                resolve({ server, port, requests });
            });
        });
    });
}

describe('CodeExecutorHandler — static surface', () => {
    test('commandPrefix is unity_execute_code', () => {
        const h = new CodeExecutorHandler();
        expect(h.commandPrefix).toBe('unity_execute_code');
    });

    test('description is non-empty', () => {
        const h = new CodeExecutorHandler();
        expect(typeof h.description).toBe('string');
        expect(h.description.length).toBeGreaterThan(0);
    });

    test('getToolDefinitions returns exactly one tool keyed "unity_execute_code" with required "code" param', () => {
        const h = new CodeExecutorHandler();
        const defs = h.getToolDefinitions();
        expect(defs).not.toBeNull();
        expect(defs!.size).toBe(1);
        const def = defs!.get('unity_execute_code');
        expect(def).toBeDefined();
        expect(def!.parameterSchema.code).toBeDefined();
    });
});

describe('CodeExecutorHandler — idempotency classification', () => {
    beforeEach(() => {
        UnityConnection.resetInstanceForTesting();
    });

    test('sendToEndpoint is invoked with idempotency=unsafe', async () => {
        const stub = await startStubServer(() => ({
            status: 200,
            body: JSON.stringify({ status: 'success', result: { output: '', returnValue: '42' } }),
        }));

        try {
            const conn = UnityConnection.getInstance();
            seedInstance(conn, `http://127.0.0.1:${stub.port}`);

            // Spy on sendToEndpoint to observe opts.
            const calls: any[] = [];
            const origSendToEndpoint = conn.sendToEndpoint.bind(conn);
            (conn as any).sendToEndpoint = async (endpoint: string, body: any, opts?: any) => {
                calls.push({ endpoint, body, opts });
                return origSendToEndpoint(endpoint, body, opts);
            };

            const h = new CodeExecutorHandler();
            h.initialize(conn);
            await h.execute('execute', { code: 'return 42;' });

            expect(calls).toHaveLength(1);
            expect(calls[0].endpoint).toBe('/execute_code');
            expect(calls[0].opts.idempotency).toBe('unsafe');
        } finally {
            await new Promise<void>((resolve) => stub.server.close(() => resolve()));
        }
    });
});

describe('CodeExecutorHandler — HTTP integration', () => {
    beforeEach(() => {
        UnityConnection.resetInstanceForTesting();
    });

    test('execute({code}) POSTs {"code":"..."} to /execute_code and returns unwrapped result', async () => {
        const stub = await startStubServer((req) => {
            // Sanity: we should see a flat {code:"..."} body (NOT /command-wrapped).
            if (req.url === '/execute_code' && req.method === 'POST') {
                return {
                    status: 200,
                    body: JSON.stringify({
                        status: 'success',
                        result: { output: '', returnValue: '42' },
                    }),
                };
            }
            return { status: 404, body: JSON.stringify({ status: 'error', error: { code: 'not_found' } }) };
        });

        try {
            const conn = UnityConnection.getInstance();
            seedInstance(conn, `http://127.0.0.1:${stub.port}`);

            const h = new CodeExecutorHandler();
            h.initialize(conn);

            const result = await h.execute('execute', { code: 'return 42;' });

            expect(stub.requests).toHaveLength(1);
            const req = stub.requests[0];
            expect(req.method).toBe('POST');
            expect(req.url).toBe('/execute_code');
            expect(req.body).toBe(JSON.stringify({ code: 'return 42;' }));

            // Unwrapped: returns result contents directly (not the envelope).
            expect(result).toEqual({ output: '', returnValue: '42' });
        } finally {
            await new Promise<void>((resolve) => stub.server.close(() => resolve()));
        }
    });

    test('target parameter is forwarded to sendToEndpoint opts.target, NOT leaked into body', async () => {
        const stub = await startStubServer(() => ({
            status: 200,
            body: JSON.stringify({ status: 'success', result: { output: 'ok' } }),
        }));

        try {
            const conn = UnityConnection.getInstance();
            // Two instances so `target` is needed for routing.
            seedInstance(conn, `http://127.0.0.1:${stub.port}`, 'Alpha-1', 'Alpha');
            seedInstance(conn, `http://127.0.0.1:${stub.port}`, 'Beta-2', 'Beta');

            const recordedOpts: any[] = [];
            const origSendToEndpoint = conn.sendToEndpoint.bind(conn);
            (conn as any).sendToEndpoint = async (endpoint: string, body: any, opts?: any) => {
                recordedOpts.push({ endpoint, body, opts });
                return origSendToEndpoint(endpoint, body, opts);
            };

            const h = new CodeExecutorHandler();
            h.initialize(conn);

            await h.execute('execute', { code: 'return 1;', target: 'Beta' });

            expect(recordedOpts).toHaveLength(1);
            expect(recordedOpts[0].opts.target).toBe('Beta');
            // Body must not contain `target` — it's routing metadata only.
            expect(recordedOpts[0].body).toEqual({ code: 'return 1;' });

            // Wire-level confirmation: the POST body on the stub server is also flat.
            expect(stub.requests).toHaveLength(1);
            expect(stub.requests[0].body).toBe(JSON.stringify({ code: 'return 1;' }));
        } finally {
            await new Promise<void>((resolve) => stub.server.close(() => resolve()));
        }
    });
});
