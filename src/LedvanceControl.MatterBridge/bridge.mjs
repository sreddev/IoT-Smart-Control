// IoT Smart Control — Matter controller sidecar.
//
// Runs a matter.js CommissioningController (its own Matter fabric, stored under --storage) and talks
// to the WPF app over stdio with newline-delimited JSON:
//
//   request  (stdin)  {"id":1,"cmd":"set","nodeId":"123","on":true,"level":40}
//   response (stdout) {"id":1,"ok":true,"result":{...}}   or   {"id":1,"ok":false,"error":"..."}
//   event    (stdout) {"event":"state","nodeId":"123","state":{...}}
//                     {"event":"connection","nodeId":"123","status":"connected"}
//
// stdout carries protocol lines only; every log line goes to stderr.
// Values on the wire are normalised (not raw Matter units):
//   level 0-100 %, hue 0-360°, saturation 0-100 %, colour temperature in Kelvin.
//
// Security: the first stdin line must be {"cmd":"init","storageKey":"<base64 AES-256 key>"}. Every value
// the controller persists (fabric root key, operational key, certificates, paired nodes) is encrypted
// with it (AES-256-GCM, bound to its storage key name) before it touches the disk; values written in
// plain text by older versions are encrypted on start. The key comes over stdin, not argv/env, which
// other processes can read. New files are owner-only (umask 077).

process.umask(0o077);

import { Environment, Logger, LogLevel, StorageService } from "@matter/main";
import { fromJson, toJson } from "@matter/general";
import { FileStorageDriver } from "@matter/nodejs";
import { createCipheriv, createDecipheriv, randomBytes } from "node:crypto";
import { ColorControl, FixedLabel, GeneralCommissioning, LevelControl, OnOff, UserLabel } from "@matter/main/clusters";
import { ManualPairingCodeCodec, NodeId, QrPairingCodeCodec } from "@matter/main/types";
import { NodeStates } from "@project-chip/matter.js/device";
import { CommissioningController } from "@project-chip/matter.js";
import { createInterface } from "node:readline";

// ------------------------------------------------------------------ arguments & logging

const args = Object.fromEntries(
    process.argv.slice(2).reduce((acc, a, i, all) => {
        if (a.startsWith("--")) acc.push([a.slice(2), all[i + 1] && !all[i + 1].startsWith("--") ? all[i + 1] : "true"]);
        return acc;
    }, []),
);

Logger.log = (_level, text) => process.stderr.write(text + "\n");
Logger.level = args.verbose === "debug" ? LogLevel.DEBUG : args.verbose === "true" ? LogLevel.INFO : LogLevel.WARN;

function send(obj) {
    process.stdout.write(JSON.stringify(obj, (_k, v) => (typeof v === "bigint" ? v.toString() : v)) + "\n");
}

// ------------------------------------------------------------------ encrypted storage

const ENC = "enc:v1:";
let storageKey; // Buffer(32), set by the init line before the controller starts

/** Binds a ciphertext to where it is stored, so values can't be swapped between files. */
const aadOf = (contexts, key) => Buffer.from([...contexts, key].join("\u001f"), "utf8");

function seal(contexts, key, value) {
    const iv = randomBytes(12);
    const cipher = createCipheriv("aes-256-gcm", storageKey, iv);
    cipher.setAAD(aadOf(contexts, key));
    const data = Buffer.concat([cipher.update(toJson(value), "utf8"), cipher.final()]);
    return ENC + Buffer.concat([iv, cipher.getAuthTag(), data]).toString("base64");
}

function open(contexts, key, stored) {
    if (typeof stored !== "string" || !stored.startsWith(ENC)) return stored; // plain value (older version)
    const raw = Buffer.from(stored.slice(ENC.length), "base64");
    try {
        const decipher = createDecipheriv("aes-256-gcm", storageKey, raw.subarray(0, 12));
        decipher.setAAD(aadOf(contexts, key));
        decipher.setAuthTag(raw.subarray(12, 28));
        return fromJson(Buffer.concat([decipher.update(raw.subarray(28)), decipher.final()]).toString("utf8"));
    } catch {
        throw new Error(`Matter storage value '${[...contexts, key].join(".")}' could not be decrypted (wrong or missing storage key).`);
    }
}

/**
 * The standard per-value file driver with every value encrypted. Registered under the id "file" so it
 * replaces the built-in driver for the same on-disk layout (no storage migration is triggered).
 */
class EncryptedFileStorageDriver extends FileStorageDriver {
    static id = "file";

    static async create(namespace) {
        const driver = new EncryptedFileStorageDriver(namespace);
        await driver.initialize();
        await driver.encryptLegacyValues();
        return driver;
    }

    async get(contexts, key) { return open(contexts, key, await super.get(contexts, key)); }

    async set(contexts, keyOrValues, value) {
        if (typeof keyOrValues === "string") return super.set(contexts, keyOrValues, seal(contexts, keyOrValues, value));
        const sealed = {};
        for (const [k, v] of Object.entries(keyOrValues)) sealed[k] = seal(contexts, k, v);
        return super.set(contexts, sealed);
    }

    async values(contexts) {
        const out = {};
        for (const [k, v] of Object.entries(await super.values(contexts))) out[k] = open(contexts, k, v);
        return out;
    }

    /** Rewrites every value still stored in plain text (from versions before storage encryption). */
    async encryptLegacyValues() {
        let count = 0;
        const walk = async contexts => {
            for (const key of contexts.length ? await this.keys(contexts) : []) {
                const raw = await super.get(contexts, key);
                if (raw !== undefined && !(typeof raw === "string" && raw.startsWith(ENC))) {
                    await this.set(contexts, key, raw);
                    count++;
                }
            }
            for (const child of await this.contexts(contexts)) await walk([...contexts, child]);
        };
        await walk([]);
        if (count > 0) log(`encrypted ${count} stored value(s) written in plain text by an older version`);
    }
}

// ------------------------------------------------------------------ controller

const environment = Environment.default;
{
    const storage = environment.get(StorageService);
    storage.registerDriver(EncryptedFileStorageDriver); // replaces the plain "file" driver
    storage.configuredDriver = "file";
}
// The filesystem root is resolved lazily from "path.root"; the fabric (keys, certificates and the
// list of paired nodes) is persisted underneath it.
if (args.storage) environment.vars.set("path.root", args.storage);
environment.vars.set("runtime.signals", false); // the parent process owns our lifetime

/** Last known normalised state per node id (string). */
const states = new Map();
/** Capabilities per node id, filled once the node's structure is known. */
const caps = new Map();

const controller = new CommissioningController({
    environment: { environment, id: "iot-smart-control" },
    autoConnect: false,
    autoSubscribe: true,
    adminFabricLabel: "IoT Smart Control",
    basicInformation: { vendorName: "IoT Smart Control", productName: "Windows Matter controller" },
    attributeChangedCallback: (nodeId, data) => onAttribute(String(nodeId), data),
    stateInformationCallback: (nodeId, info) => onNodeState(String(nodeId), info),
});

const STATE_NAMES = { 0: "connected", 1: "disconnected", 2: "reconnecting", 3: "waiting", 4: "structureChanged", 5: "decommissioned" };

function onNodeState(id, info) {
    const status = STATE_NAMES[info] ?? String(info);
    send({ event: "connection", nodeId: id, status });
    if (status === "connected" || status === "structureChanged") {
        refreshNode(id).catch(e => log(`refresh ${id} failed: ${e?.message ?? e}`));
    }
}

function onAttribute(id, data) {
    const name = data?.path?.attributeName;
    const value = data?.value;
    if (name === undefined || value === undefined) return;
    const s = states.get(id) ?? {};
    const c = caps.get(id);
    switch (name) {
        case "onOff": s.on = !!value; break;
        case "currentLevel": if (value !== null) s.level = levelToPercent(value); break;
        case "currentHue": s.hue = Math.round((value * 360) / 254); break;
        case "currentSaturation": s.saturation = Math.round((value * 100) / 254); break;
        case "colorTemperatureMireds": if (value > 0) s.colorTempKelvin = Math.round(1e6 / value); break;
        case "colorMode": s.colorMode = COLOR_MODES[value] ?? String(value); break;
        case "currentX": s._x = value; break;
        case "currentY": s._y = value; break;
        default: return;
    }
    if ((name === "currentX" || name === "currentY") && c && !c.hs && s._x !== undefined && s._y !== undefined) {
        const { h, s: sat } = xyToHs(s._x / 65536, s._y / 65536);
        s.hue = h; s.saturation = sat;
    }
    states.set(id, s);
    send({ event: "state", nodeId: id, state: publicState(s) });
}

const COLOR_MODES = { 0: "hs", 1: "xy", 2: "ct" };

function publicState(s) {
    const { _x, _y, ...rest } = s;
    return rest;
}

// ------------------------------------------------------------------ node helpers

async function pairedNode(id, timeoutMs = 20000) {
    const nodeId = NodeId(BigInt(id));
    if (!controller.isNodeCommissioned(nodeId)) throw new Error(`Matter node ${id} is not paired with this PC.`);
    let node = controller.getPairedNode(nodeId);
    if (!node) node = await controller.connectNode(nodeId);
    const t0 = Date.now();
    while (!node.initialized && !node.localInitializationDone) {
        if (Date.now() - t0 > timeoutMs) throw new Error(`Matter node ${id} did not respond (is the bulb powered and on Wi-Fi?).`);
        await new Promise(r => setTimeout(r, 200));
    }
    return node;
}

/** Finds the first endpoint that carries an On/Off cluster (the light). */
function lightEndpoint(node) {
    const stack = [...node.getDevices()];
    while (stack.length) {
        const ep = stack.shift();
        if (ep.getClusterClient(OnOff.Cluster)) return ep;
        stack.push(...ep.getChildEndpoints());
    }
    return undefined;
}

async function readCaps(id, ep) {
    const c = { onOff: true, level: false, hs: false, xy: false, ct: false, ctMinKelvin: 2700, ctMaxKelvin: 6500 };
    if (ep.getClusterClient(LevelControl.Cluster)) c.level = true;
    const cc = ep.getClusterClient(ColorControl.Cluster);
    if (cc) {
        const cap = await safe(() => cc.getColorCapabilitiesAttribute());
        if (cap) {
            c.hs = !!cap.hueSaturation;
            c.xy = !!cap.xy;
            c.ct = !!cap.colorTemperature;
        } else {
            c.hs = true; // capability attribute unreadable: assume the common case
        }
        if (c.ct) {
            const minM = await safe(() => cc.getColorTempPhysicalMinMiredsAttribute());
            const maxM = await safe(() => cc.getColorTempPhysicalMaxMiredsAttribute());
            if (maxM > 0) c.ctMinKelvin = Math.round(1e6 / maxM);
            if (minM > 0) c.ctMaxKelvin = Math.round(1e6 / minM);
        }
    }
    caps.set(id, c);
    return c;
}

/** Reads the current light state from the device (fresh, not cached). */
async function readState(id, ep) {
    const c = caps.get(id) ?? (await readCaps(id, ep));
    const s = states.get(id) ?? {};
    const onOff = ep.getClusterClient(OnOff.Cluster);
    const on = await safe(() => onOff.getOnOffAttribute(true));
    if (on !== undefined) s.on = !!on;
    if (c.level) {
        const lvl = await safe(() => ep.getClusterClient(LevelControl.Cluster).getCurrentLevelAttribute(true));
        if (typeof lvl === "number") s.level = levelToPercent(lvl);
    }
    const cc = ep.getClusterClient(ColorControl.Cluster);
    if (cc) {
        const mode = await safe(() => cc.getColorModeAttribute(true));
        if (mode !== undefined) s.colorMode = COLOR_MODES[mode] ?? String(mode);
        if (c.hs) {
            const h = await safe(() => cc.getCurrentHueAttribute(true));
            const sat = await safe(() => cc.getCurrentSaturationAttribute(true));
            if (typeof h === "number") s.hue = Math.round((h * 360) / 254);
            if (typeof sat === "number") s.saturation = Math.round((sat * 100) / 254);
        } else if (c.xy) {
            const x = await safe(() => cc.getCurrentXAttribute(true));
            const y = await safe(() => cc.getCurrentYAttribute(true));
            if (typeof x === "number" && typeof y === "number") Object.assign(s, xyToHs(x / 65536, y / 65536));
        }
        if (c.ct) {
            const m = await safe(() => cc.getColorTemperatureMiredsAttribute(true));
            if (m > 0) s.colorTempKelvin = Math.round(1e6 / m);
        }
    }
    states.set(id, s);
    return publicState(s);
}

async function refreshNode(id) {
    const node = await pairedNode(id);
    const ep = lightEndpoint(node);
    if (!ep) return;
    await readCaps(id, ep);
    send({ event: "state", nodeId: id, state: await readState(id, ep) });
}

function describe(id, node) {
    const info = safeSync(() => node.basicInformation) ?? {};
    return {
        nodeId: id,
        name: info.nodeLabel || info.productLabel || info.productName || `Matter device ${id}`,
        vendorName: info.vendorName,
        productName: info.productName,
        vendorId: info.vendorId,
        productId: info.productId,
        serialNumber: info.serialNumber,
        softwareVersion: info.softwareVersionString,
        connected: node.connectionState === NodeStates.Connected,
        capabilities: caps.get(id),
    };
}

// ------------------------------------------------------------------ commands

// Fade for level/colour changes, in tenths of a second. 0 = change at once. Measured on LEDVANCE
// SMART MAT bulbs (fw 1.1.0): with 0 they reach the new level in ~0.2 s, but ANY non-zero value
// (even 0.1 s), or none, makes them ramp slowly over ~4.5-5 s, which felt like a 2-3 s lag.
const TRANSITION = 0;

const handlers = {
    async ping() {
        return { version: 1, fabricLabel: "IoT Smart Control", nodes: controller.getCommissionedNodes().map(String) };
    },

    /** Looks for devices that currently have a commissioning (pairing) window open on the LAN. */
    async discover({ timeoutSeconds = 8 }) {
        const found = await controller.discoverCommissionableDevices({}, { onIpNetwork: true }, undefined, timeoutSeconds * 1000);
        return found.map(d => ({
            deviceIdentifier: d.deviceIdentifier,
            discriminator: d.D,
            vendorId: d.V,
            productId: d.P,
            deviceName: d.DN,
            commissioningMode: d.CM,
            addresses: (d.addresses ?? []).map(a => `${a.ip}:${a.port}`),
        }));
    },

    /** Pairs a device into this PC's fabric using its 11/21-digit manual code or MT:… QR payload. */
    async commission({ pairingCode, ip, port }) {
        if (!pairingCode) throw new Error("A pairing code is required.");
        const code = String(pairingCode).trim();
        let passcode, identifierData;
        if (code.toUpperCase().startsWith("MT:")) {
            const qr = QrPairingCodeCodec.decode(code)[0];
            passcode = qr.passcode;
            identifierData = { longDiscriminator: qr.discriminator };
        } else {
            const digits = code.replace(/[^0-9]/g, "");
            if (digits.length !== 11 && digits.length !== 21) throw new Error("A manual pairing code has 11 or 21 digits.");
            const m = ManualPairingCodeCodec.decode(digits);
            passcode = m.passcode;
            identifierData = m.discriminator !== undefined ? { longDiscriminator: m.discriminator } : { shortDiscriminator: m.shortDiscriminator };
        }
        const nodeId = await controller.commissionNode({
            commissioning: {
                regulatoryLocation: GeneralCommissioning.RegulatoryLocationType.IndoorOutdoor,
                regulatoryCountryCode: "XX",
            },
            discovery: {
                identifierData,
                discoveryCapabilities: { onIpNetwork: true },
                knownAddress: ip ? { ip, port: Number(port) || 5540, type: "udp" } : undefined,
                timeout: 60_000,
            },
            passcode,
        });
        const id = String(nodeId);
        const node = await pairedNode(id, 60000);
        const ep = lightEndpoint(node);
        if (ep) {
            await readCaps(id, ep);
            states.set(id, {});
            await readState(id, ep);
        }
        return { ...describe(id, node), state: publicState(states.get(id) ?? {}) };
    },

    /** Lists every device paired with this PC (connects to them in the background). */
    async nodes() {
        const out = [];
        for (const nodeId of controller.getCommissionedNodes()) {
            const id = String(nodeId);
            let node = controller.getPairedNode(nodeId);
            if (!node) {
                try { node = await controller.connectNode(nodeId); }
                catch (e) { out.push({ nodeId: id, name: `Matter device ${id}`, connected: false, error: e?.message ?? String(e) }); continue; }
            }
            if (!caps.has(id) && (node.initialized || node.localInitializationDone)) {
                const ep = lightEndpoint(node);
                if (ep) await safe(() => readCaps(id, ep));
            }
            out.push(describe(id, node));
        }
        return out;
    },

    async get({ nodeId }) {
        const node = await pairedNode(nodeId);
        // Right after start-up the CASE session may still be connecting: give it a moment instead of
        // reporting the bulb offline (the app's launch refresh lands exactly in that window).
        for (const t0 = Date.now(); node.connectionState !== NodeStates.Connected && Date.now() - t0 < 12000;)
            await new Promise(r => setTimeout(r, 200));
        const ep = lightEndpoint(node);
        if (!ep) throw new Error("This Matter device has no On/Off endpoint.");
        return { ...describe(String(nodeId), node), state: await readState(String(nodeId), ep) };
    },

    /** Applies any of: on, level (%), hue (°) + saturation (%), colorTempKelvin. */
    async set({ nodeId, on, level, hue, saturation, colorTempKelvin, transition }) {
        const id = String(nodeId);
        const node = await pairedNode(id);
        const ep = lightEndpoint(node);
        if (!ep) throw new Error("This Matter device has no On/Off endpoint.");
        const c = caps.get(id) ?? (await readCaps(id, ep));
        const tt = typeof transition === "number" ? Math.round(transition * 10) : TRANSITION;
        const opts = { optionsMask: {}, optionsOverride: {} };
        const s = states.get(id) ?? {};

        const onOff = ep.getClusterClient(OnOff.Cluster);
        if (on === false) {
            // Plain Off uses the bulb's built-in fade (these bulbs don't let us read or set it);
            // OffWithEffect "no fade" switches off at once.
            if (onOff.supportedFeatures?.lighting) {
                await onOff.offWithEffect({ effectIdentifier: OnOff.EffectIdentifier.DelayedAllOff, effectVariant: OnOff.DelayedAllOffEffectVariant.NoFade });
            } else {
                await onOff.off();
            }
            s.on = false;
            states.set(id, s);
            return publicState(s);
        }
        const lc = c.level ? ep.getClusterClient(LevelControl.Cluster) : undefined;
        const cc = ep.getClusterClient(ColorControl.Cluster);
        // One command per aspect, one after another (each is acknowledged in ~30-60 ms).
        if (typeof level === "number" && lc) {
            // moveToLevelWithOnOff also switches the light on
            const raw = Math.min(254, Math.max(1, Math.round((level * 254) / 100)));
            await lc.moveToLevelWithOnOff({ level: raw, transitionTime: tt, ...opts });
            s.level = Math.round(level);
            s.on = true;
        } else if (on === true) {
            // Plain On fades in with the bulb's own (slow) timing. "Move to the brightness it has, switching
            // on, in 0 s" lights it at once; the bulb keeps CurrentLevel while off, so read it fresh.
            const raw = lc ? await safe(() => lc.getCurrentLevelAttribute(true)) : undefined;
            if (typeof raw === "number" && raw > 0) {
                await lc.moveToLevelWithOnOff({ level: raw, transitionTime: 0, ...opts });
                s.level = levelToPercent(raw);
            } else {
                await onOff.on();
            }
            s.on = true;
        }
        if (cc && typeof hue === "number" && typeof saturation === "number") {
            if (c.hs) {
                await cc.moveToHueAndSaturation({
                    hue: Math.min(254, Math.max(0, Math.round((((hue % 360) + 360) % 360) * 254 / 360))),
                    saturation: Math.min(254, Math.max(0, Math.round((saturation * 254) / 100))),
                    transitionTime: tt, ...opts,
                });
                s.colorMode = "hs";
            } else if (c.xy) {
                const { x, y } = hsToXy(hue, saturation);
                await cc.moveToColor({
                    colorX: Math.min(65279, Math.round(x * 65536)),
                    colorY: Math.min(65279, Math.round(y * 65536)),
                    transitionTime: tt, ...opts,
                });
                s.colorMode = "xy";
            }
            s.hue = Math.round(hue);
            s.saturation = Math.round(saturation);
        } else if (cc && typeof colorTempKelvin === "number" && c.ct) {
            const k = Math.min(c.ctMaxKelvin, Math.max(c.ctMinKelvin, colorTempKelvin));
            await cc.moveToColorTemperature({ colorTemperatureMireds: Math.round(1e6 / k), transitionTime: tt, ...opts });
            s.colorTempKelvin = Math.round(k);
            s.colorMode = "ct";
        }
        states.set(id, s);
        return publicState(s);
    },

    /** Diagnostic dump: basic information and every label list the device exposes, per endpoint. */
    async inspect({ nodeId }) {
        const node = await pairedNode(String(nodeId));
        const out = { basicInformation: safeSync(() => node.basicInformation), endpoints: [] };
        const stack = [...node.getDevices()];
        const root = node.getRootEndpoint?.();
        if (root) stack.unshift(root);
        const seen = new Set();
        while (stack.length) {
            const ep = stack.shift();
            const num = ep.getNumber();
            if (seen.has(num)) continue;
            seen.add(num);
            const entry = { endpoint: num, deviceTypes: ep.getDeviceTypes().map(t => t.name), clusters: ep.getAllClusterClients().map(c => c.name) };
            const ul = ep.getClusterClient(UserLabel.Cluster);
            if (ul) entry.userLabels = await safe(() => ul.getLabelListAttribute(true));
            const fl = ep.getClusterClient(FixedLabel.Cluster);
            if (fl) entry.fixedLabels = await safe(() => fl.getLabelListAttribute(true));
            // the bulb's own fades (tenths of a second): plain On/Off commands use these, not our transitionTime
            const lc = ep.getClusterClient(LevelControl.Cluster);
            if (lc) entry.transitions = {
                onOffTransitionTime: await safe(() => lc.getOnOffTransitionTimeAttribute(true)),
                onTransitionTime: await safe(() => lc.getOnTransitionTimeAttribute(true)),
                offTransitionTime: await safe(() => lc.getOffTransitionTimeAttribute(true)),
            };
            const oo = ep.getClusterClient(OnOff.Cluster);
            if (oo) entry.onOffLighting = !!oo.supportedFeatures?.lighting;
            out.endpoints.push(entry);
            stack.push(...ep.getChildEndpoints());
        }
        return out;
    },

    /** Removes this PC from the device (the device stays paired with its other controllers). */
    async remove({ nodeId }) {
        const n = NodeId(BigInt(nodeId));
        try {
            await controller.removeNode(n, true); // also deletes our fabric from the device
        } catch (e) {
            log(`decommission of ${nodeId} failed (${e?.message ?? e}); forgetting it locally`);
            await controller.removeNode(n, false);
        }
        states.delete(String(nodeId));
        caps.delete(String(nodeId));
        return { removed: String(nodeId) };
    },

    async shutdown() {
        setTimeout(() => shutdown(0), 50);
        return { bye: true };
    },
};

// ------------------------------------------------------------------ colour maths

function levelToPercent(raw) {
    return Math.max(0, Math.min(100, Math.round((raw * 100) / 254)));
}

function hsToXy(hueDeg, satPct) {
    const [r, g, b] = hsvToRgb(hueDeg, satPct / 100, 1).map(srgbToLinear);
    const X = r * 0.4124 + g * 0.3576 + b * 0.1805;
    const Y = r * 0.2126 + g * 0.7152 + b * 0.0722;
    const Z = r * 0.0193 + g * 0.1192 + b * 0.9505;
    const sum = X + Y + Z;
    return sum === 0 ? { x: 0.3127, y: 0.329 } : { x: X / sum, y: Y / sum };
}

function xyToHs(x, y) {
    if (y <= 0) return { hue: 0, saturation: 0 };
    const Y = 1, X = (x / y) * Y, Z = ((1 - x - y) / y) * Y;
    let r = X * 3.2406 - Y * 1.5372 - Z * 0.4986;
    let g = -X * 0.9689 + Y * 1.8758 + Z * 0.0415;
    let b = X * 0.0557 - Y * 0.204 + Z * 1.057;
    const m = Math.max(r, g, b, 1e-9);
    [r, g, b] = [r, g, b].map(v => linearToSrgb(Math.max(0, v / m)));
    const max = Math.max(r, g, b), min = Math.min(r, g, b), d = max - min;
    let h = 0;
    if (d > 0) h = max === r ? 60 * (((g - b) / d) % 6) : max === g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
    return { hue: Math.round((h + 360) % 360), saturation: Math.round(max === 0 ? 0 : (d / max) * 100) };
}

function hsvToRgb(h, s, v) {
    h = ((h % 360) + 360) % 360 / 60;
    const c = v * s, x = c * (1 - Math.abs((h % 2) - 1)), m = v - c;
    const [r, g, b] = h < 1 ? [c, x, 0] : h < 2 ? [x, c, 0] : h < 3 ? [0, c, x] : h < 4 ? [0, x, c] : h < 5 ? [x, 0, c] : [c, 0, x];
    return [r + m, g + m, b + m];
}

const srgbToLinear = v => (v <= 0.04045 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4);
const linearToSrgb = v => (v <= 0.0031308 ? 12.92 * v : 1.055 * v ** (1 / 2.4) - 0.055);

// ------------------------------------------------------------------ plumbing

async function safe(fn) {
    try { return await fn(); } catch { return undefined; }
}
function safeSync(fn) {
    try { return fn(); } catch { return undefined; }
}
function log(msg) {
    process.stderr.write(`[bridge] ${msg}\n`);
}

let closing = false;
async function shutdown(code) {
    if (closing) return;
    closing = true;
    try { await controller.close(); } catch { /* ignore */ }
    process.exit(code);
}

// The first stdin line carries the storage key; nothing touches the storage before it arrives.
const rl = createInterface({ input: process.stdin });
const init = await new Promise(resolve => {
    rl.once("line", line => { try { resolve(JSON.parse(line)); } catch { resolve(null); } });
    rl.once("close", () => resolve(null));
});
const keyBytes = init?.cmd === "init" && typeof init.storageKey === "string" ? Buffer.from(init.storageKey, "base64") : null;
if (!keyBytes || keyBytes.length !== 32) {
    log("refusing to start: the first line must be an init message with a 32-byte base64 storageKey");
    process.exit(2);
}
storageKey = keyBytes;

try {
    await controller.start();
} catch (e) {
    log(`Error: could not open the Matter storage: ${e?.message ?? e}`);
    process.exit(3);
}

// Connect every paired node in the background; state arrives as events.
for (const nodeId of controller.getCommissionedNodes()) {
    controller.connectNode(nodeId).catch(e => log(`connect ${nodeId} failed: ${e?.message ?? e}`));
}

send({ event: "ready", nodes: controller.getCommissionedNodes().map(String) });

rl.on("line", async line => {
    if (!line.trim()) return;
    let req;
    try { req = JSON.parse(line); }
    catch { send({ id: null, ok: false, error: "Invalid JSON request." }); return; }
    const handler = handlers[req.cmd];
    if (!handler) { send({ id: req.id, ok: false, error: `Unknown command '${req.cmd}'.` }); return; }
    try {
        send({ id: req.id, ok: true, result: await handler(req) });
    } catch (e) {
        send({ id: req.id, ok: false, error: e?.message ?? String(e) });
    }
});
rl.on("close", () => shutdown(0)); // parent exited or closed our stdin
process.on("SIGTERM", () => shutdown(0));
