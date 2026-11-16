// Bundles bridge.mjs and its matter.js dependencies into a single dist/bridge.mjs, so the app ships
// one file instead of ~27k node_modules files. Run with: npm run build
import { build } from "esbuild";

await build({
    entryPoints: ["bridge.mjs"],
    outfile: "dist/bridge.mjs",
    bundle: true,
    platform: "node",
    target: "node20",
    format: "esm",
    legalComments: "linked", // keeps third-party licence notices in dist/bridge.mjs.LEGAL.txt
    logLevel: "warning",
    // matter.js ships a Bun-only storage driver that imports "bun:sqlite" statically. It is never used
    // under Node, but an unresolvable import would fail at load time, so replace it with an empty stub.
    plugins: [{
        name: "stub-bun",
        setup(b) {
            b.onResolve({ filter: /^bun:/ }, args => ({ path: args.path, namespace: "bun-stub" }));
            b.onLoad({ filter: /.*/, namespace: "bun-stub" }, () => ({ contents: "module.exports = {};", loader: "js" }));
        },
    }],
    // Some dependencies are CommonJS and call require(); give the ESM bundle a real one.
    banner: { js: "import { createRequire as __createRequire } from 'node:module'; const require = __createRequire(import.meta.url);" },
});
console.log("built dist/bridge.mjs");
