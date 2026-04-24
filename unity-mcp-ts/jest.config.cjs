/**
 * Jest configuration for unity-mcp-ts.
 *
 * The production code is authored as ESM (type: module, .js import specifiers).
 * We use ts-jest's ESM preset and a tiny regex to rewrite relative `.js`
 * specifiers to their `.ts` equivalents at test time, so that Jest can resolve
 * modules without a build step.
 */
module.exports = {
    preset: 'ts-jest/presets/default-esm',
    testEnvironment: 'node',
    extensionsToTreatAsEsm: ['.ts'],
    moduleNameMapper: {
        '^(\\.{1,2}/.*)\\.js$': '$1',
    },
    transform: {
        '^.+\\.tsx?$': [
            'ts-jest',
            {
                useESM: true,
                tsconfig: {
                    module: 'ESNext',
                    moduleResolution: 'Node',
                    target: 'ES2022',
                    esModuleInterop: true,
                    strict: true,
                    skipLibCheck: true,
                    resolveJsonModule: true,
                },
            },
        ],
    },
    testMatch: ['<rootDir>/src/__tests__/**/*.test.ts'],
    testTimeout: 15000,
};
