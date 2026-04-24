/**
 * Tests for CodePromptHandler (built-in MCP prompt `code_execute`).
 *
 * Coverage:
 *   - promptName / description static surface
 *   - getPromptDefinitions contains `code_execute` entry
 *   - Template spot-check: must contain "return" statement guidance and the
 *     `namespace CodeExecutionContainer` wrapper marker, so MCP clients get
 *     the correct code shape.
 */
import { describe, test, expect } from '@jest/globals';
import { CodePromptHandler } from '../handlers/CodePromptHandler.js';

describe('CodePromptHandler', () => {
    test('promptName is "code"', () => {
        const h = new CodePromptHandler();
        expect(h.promptName).toBe('code');
    });

    test('description is non-empty', () => {
        const h = new CodePromptHandler();
        expect(typeof h.description).toBe('string');
        expect(h.description.length).toBeGreaterThan(0);
    });

    test('getPromptDefinitions returns a Map containing "code_execute"', () => {
        const h = new CodePromptHandler();
        const defs = h.getPromptDefinitions();
        expect(defs).not.toBeNull();
        expect(defs!.has('code_execute')).toBe(true);
    });

    test('code_execute template contains key guidance strings', () => {
        const h = new CodePromptHandler();
        const defs = h.getPromptDefinitions();
        const def = defs!.get('code_execute');
        expect(def).toBeDefined();

        // Spot-check: critical keywords the Samples JS template promised.
        expect(def!.template).toContain('return');
        expect(def!.template).toContain('CodeExecutionContainer');
    });

    test('code_execute template mentions how to return values', () => {
        const h = new CodePromptHandler();
        const def = h.getPromptDefinitions()!.get('code_execute')!;
        // Guidance item #4 in the template, ported verbatim from JS sample.
        expect(def.template).toMatch(/return.*statement/i);
    });
});
