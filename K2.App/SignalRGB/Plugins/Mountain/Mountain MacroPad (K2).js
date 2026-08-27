// Mountain MacroPad — K2 plugin for SignalRGB
//
// Replacement for SignalRGB's bundled "Mountain_Macropad.js", which lights the 12 keys
// only partially: some respond to the canvas, some stay black, some keep running the
// keyboard's own stored effect. Root-caused from a USB capture of Base Camp painting the
// pad one key at a time (K2's _reference/usb_dumps/macropad_custom.pcapng, 2026-08-27).
//
// Base Camp's sequence per apply, byte for byte:
//   14 2C 0A 00 FF 64 FF...          enable Custom mode / master brightness, 0xFF padded
//   11 01 00 02 01 02                zone switch -> zone 02 (keycaps)   <-- the missing piece
//   14 2C 00 01 00 <bright> 00 + RGB one page, 12 LED slots, contiguous indices 0..11
//   13 55 00 00 06                   persist to flash slot 6
// The bundled plugin never sends the zone switch, so the firmware never fully enters
// Custom and honours the colour page only in part. It also sends the enable packet with
// NO report-ID byte and length 64 while every other write uses 0x00 + 65, so that packet
// is shifted by one byte on the wire — hence its own comment, "WHY DOES THIS WORK?".
//
// Capture-confirmed LED map: 12 applies painting M1..M12 in order lit slots 0..11 in
// order. Contiguous, single page, no paging — the bundled plugin's vKeys was already
// right, the addressing was never the problem.
//
// Deliberately NOT replicated from Base Camp:
//   * 13 55 00 00 06 (persist to flash). BC does it because an apply is a one-off user
//     action; a plugin streaming at frame rate would burn the flash.
//   * device.write([0x00, 0x11], 65) + device.addFeature("keyboard") + the key-report
//     reader. That is the bundled plugin turning the pad's keys into SignalRGB input
//     events, which would double up with K2's own key bindings and macros. This plugin is
//     lighting only: K2 keeps the keys, SignalRGB gets the LEDs.
//
// Install via K2: Settings > SignalRGB > "Install K2 plugins".

export function Name() { return "Mountain MacroPad (K2)"; }
export function VendorId() { return 0x3282; }
export function ProductId() { return 0x0008; }
export function Publisher() { return "K2"; }
export function Size() { return [6, 2]; }
export function DefaultPosition() { return [10, 100]; }
const DESIRED_HEIGHT = 40;
export function DefaultScale() { return Math.floor(DESIRED_HEIGHT / Size()[1]); }
export function DeviceType() { return "keyboard"; }
/* global
shutdownColor:readonly
LightingMode:readonly
forcedColor:readonly
deviceBrightness:readonly
packetDelay:readonly
*/
export function ControllableParameters() {
	return [
		{"property":"shutdownColor", "group":"lighting", "label":"Shutdown Color", description: "This color is applied to the device when the System, or SignalRGB is shutting down", "min":"0", "max":"360", "type":"color", "default":"#000000"},
		{"property":"LightingMode", "group":"lighting", "label":"Lighting Mode", description: "Determines where the device's RGB comes from. Canvas will pull from the active Effect, while Forced will override it to a specific color", "type":"combobox", "values":["Canvas", "Forced"], "default":"Canvas"},
		{"property":"forcedColor", "group":"lighting", "label":"Forced Color", description: "The color used when 'Forced' Lighting Mode is enabled", "min":"0", "max":"360", "type":"color", "default":"#009bde"},
		{"property":"deviceBrightness", "group":"lighting", "label":"Device Brightness", description: "Firmware brightness applied on top of the canvas colors (0-100). Base Camp's own slider sends this same byte; the bundled plugin hardcodes 75", "step":"5", "type":"number", "min":"0", "max":"100", "default":"100"},
		{"property":"packetDelay", "group":"lighting", "label":"Packet Delay (ms)", description: "Pause after each USB packet. A frame is 2 packets here, so this barely costs anything - raise it only if the LEDs flicker or lag behind the canvas", "step":"1", "type":"number", "min":"0", "max":"10", "default":"1"},
	];
}

// ---------------------------------------------------------------- LED map
// Wire index per key: position in the 14 2C 00 01 page stream IS the LED index.
// Capture-confirmed contiguous 0..11 in M1..M12 order.
const vKeys = [
	0, 1, 2, 3, 4, 5,
	6, 7, 8, 9, 10, 11
];

const vKeyNames = [
	"M1", "M2", "M3", "M4", "M5", "M6",
	"M7", "M8", "M9", "M10", "M11", "M12"
];

const vKeyPositions = [
	[0, 0], [1, 0], [2, 0], [3, 0], [4, 0], [5, 0],
	[0, 1], [1, 1], [2, 1], [3, 1], [4, 1], [5, 1]
];

export function LedNames() { return vKeyNames; }
export function LedPositions() { return vKeyPositions; }

// ---------------------------------------------------------------- device I/O

const LED_SLOTS = 12;
const ZONE_KEYCAPS = 0x02;

function brightnessByte() {
	const b = Math.round(Number(deviceBrightness));
	if (isNaN(b)) return 100;
	return Math.max(0, Math.min(100, b));
}

function packetPause() {
	const d = Math.round(Number(packetDelay));
	if (isNaN(d)) return 1;
	return Math.max(0, Math.min(10, d));
}

/** Every command is echoed back on the IN endpoint (capture-confirmed: 14 2C... out at
 *  frame 215 comes back at 217, the zone switch at 219 comes back at 221 with its two
 *  argument bytes zeroed). Nothing here consumes those echoes otherwise, and they share
 *  the endpoint with the pad's key-state reports, so drain the queue once per frame or it
 *  backs up. The bundled plugin gets this for free inside its key-event reader; this
 *  plugin has no key handling, so it drains explicitly. */
function drainInput() {
	do {
		device.read([0x00], 65, 0);
	}
	while (device.getLastReadSize() > 0);
}

/** 14 2C 0A 00 FF <brightness>, rest 0xFF — padding copied byte-for-byte from the
 *  capture. Sent with the report-ID byte and length 65, unlike the bundled plugin. */
function enableCustom() {
	const pkt = [0x00, 0x14, 0x2C, 0x0A, 0x00, 0xFF, 0x64];
	while (pkt.length < 65) pkt.push(0xFF);
	device.write(pkt, 65);
	if (packetPause() > 0) device.pause(packetPause());
}

/** 11 01 00 <zone> 01 02. Note byte 4 is 0x01 on the MacroPad where the Everest Max uses
 *  0x02 — the two are not interchangeable. */
function switchZone(zone) {
	device.write([0x00, 0x11, 0x01, 0x00, zone, 0x01, 0x02], 65);
	if (packetPause() > 0) device.pause(packetPause());
}

export function Initialize() {
	enableCustom();
	drainInput();
	oldColors = [];
}

let savedResetTimer = Date.now();
const PollModeInterval = 300000;

export function Render() {
	if (Date.now() - savedResetTimer >= PollModeInterval) {
		savedResetTimer = Date.now();
		enableCustom();
	}
	sendColors();
	drainInput();
}

export function Shutdown(SystemSuspending) {
	sendColors(SystemSuspending ? "#000000" : shutdownColor);
	drainInput();
}

export function ondeviceBrightnessChanged() {
	// The brightness byte rides in the colour page header, so a change has to force a
	// resend even when no colour moved.
	oldColors = [];
}

function colorFor(pos, overrideColor) {
	if (overrideColor) return hexToRgb(overrideColor);
	if (LightingMode === "Forced") return hexToRgb(forcedColor);
	return device.color(pos[0], pos[1]);
}

function grabColors(overrideColor) {
	const rgb = new Array(LED_SLOTS * 3).fill(0);
	for (let i = 0; i < vKeys.length; i++) {
		const c = colorFor(vKeyPositions[i], overrideColor);
		const o = vKeys[i] * 3;
		rgb[o] = c[0]; rgb[o + 1] = c[1]; rgb[o + 2] = c[2];
	}
	return rgb;
}

function sameArray(a, b) {
	return a.length === b.length && a.every(function(v, i) { return v === b[i]; });
}

let oldColors = [];

function sendColors(overrideColor) {
	const rgb = grabColors(overrideColor);
	if (sameArray(rgb, oldColors)) return;
	oldColors = rgb;

	// Base Camp re-sends the zone switch before EVERY colour page, so this plugin does
	// too rather than doing it once at init: the whole symptom this plugin exists to fix
	// is what happens when the firmware is not in the keycap zone, and at 2 packets per
	// frame the insurance is nearly free. If testing shows the zone is sticky, this can
	// move into Initialize().
	switchZone(ZONE_KEYCAPS);
	device.write([0x00, 0x14, 0x2C, 0x00, 0x01, 0x00, brightnessByte(), 0x00].concat(rgb), 65);
	if (packetPause() > 0) device.pause(packetPause());
}

function hexToRgb(hex) {
	const result = /^#?([a-f\d]{2})([a-f\d]{2})([a-f\d]{2})$/i.exec(hex);
	if (!result) return [0, 0, 0];
	return [parseInt(result[1], 16), parseInt(result[2], 16), parseInt(result[3], 16)];
}

export function Validate(endpoint) {
	return endpoint.interface === 2;
}

export function ImageUrl() {
	return "https://assets.signalrgb.com/devices/brands/mountain/keyboards/macropad.png";
}
