// Mountain Everest Max — K2 plugin for SignalRGB
//
// Drop-in replacement for SignalRGB's bundled "Mountain Everest Max.js". Same device,
// same VID/PID/interface, but it also drives the 45 perimeter "border" LEDs (the ring
// around the keyboard and around the numpad bezel) that the bundled plugin leaves dark,
// and it exposes the firmware brightness the bundled plugin hardcodes to 75.
//
// Protocol source: K2's own USB captures of Base Camp (see K2's EverestSideLedProtocol.cs
// — evmax_anchors_bc / evmax_numpad_bc / evmax_fillall_bc, 2026-07-22), not guesswork:
//   11 01 00 <zone> 02 02                 switch a zone to Custom (02 = keycaps, 05 = border)
//   14 2C 0A 00 FF <brightness>           enable Custom mode / master brightness (0-100)
//   14 2C 00 01 <page> <brightness> 00 +  19 RGB triplets, 7 pages -> 126 keycap LEDs
//   14 2D 0A 00 <chunk> FF 00 +           19/19/7 RGB triplets, 3 chunks -> 45 border LEDs
// Deliberately NOT sent: 13 55 00 00 06 (persist to flash). A streaming plugin must never
// write flash — that is the Base Camp "save" path and it would wear the chip out.
//
// Keycap LED indices and the base key layout are SignalRGB's own (field-proven); the
// border ring's wire indices come from K2. Install via K2: Settings > SignalRGB >
// "Install K2 plugins" (copies into Documents\WhirlwindFX\Plugins, which overrides the
// bundled folder and survives SignalRGB updates).

export function Name() { return "Mountain Everest Max Keyboard (K2)"; }
export function VendorId() { return 0x3282; }
export function ProductId() { return 0x0001; }
export function Publisher() { return "K2"; }
export function Size() { return [26, 8]; }
export function DefaultPosition() { return [10, 100]; }
const DESIRED_HEIGHT = 110;
export function DefaultScale() { return Math.floor(DESIRED_HEIGHT / Size()[1]); }
export function DeviceType() { return "keyboard"; }
/* global
shutdownColor:readonly
LightingMode:readonly
forcedColor:readonly
layout:readonly
deviceBrightness:readonly
borderLeds:readonly
packetDelay:readonly
*/
export function ControllableParameters() {
	return [
		{"property":"shutdownColor", "group":"lighting", "label":"Shutdown Color", description: "This color is applied to the device when the System, or SignalRGB is shutting down", "min":"0", "max":"360", "type":"color", "default":"#000000"},
		{"property":"LightingMode", "group":"lighting", "label":"Lighting Mode", description: "Determines where the device's RGB comes from. Canvas will pull from the active Effect, while Forced will override it to a specific color", "type":"combobox", "values":["Canvas", "Forced"], "default":"Canvas"},
		{"property":"forcedColor", "group":"lighting", "label":"Forced Color", description: "The color used when 'Forced' Lighting Mode is enabled", "min":"0", "max":"360", "type":"color", "default":"#009bde"},
		{"property":"layout", "group":"lighting", "label":"Numpad Location", description: "Which side the detachable numpad sits on, so the layout matches your desk", "type":"combobox", "values":["Left", "Right"], "default":"Right"},
		{"property":"deviceBrightness", "group":"lighting", "label":"Device Brightness", description: "Firmware brightness applied on top of the canvas colors (0-100). The bundled plugin hardcodes 75", "step":"5", "type":"number", "min":"0", "max":"100", "default":"100"},
		{"property":"borderLeds", "group":"lighting", "label":"Border LEDs", description: "Drive the 45 perimeter LEDs around the keyboard and the numpad bezel. Turn off to leave the border to the keyboard's own stored effect", "type":"boolean", "default":"true"},
		{"property":"packetDelay", "group":"lighting", "label":"Packet Delay (ms)", description: "Pause after each USB packet. This is the frame rate knob: a full frame is up to 10 packets, so 5ms caps you at ~20fps and looks like stuttering. Lower it until colour transitions are smooth; raise it again if LEDs start dropping or flickering", "step":"1", "type":"number", "min":"0", "max":"10", "default":"1"},
	];
}

// ---------------------------------------------------------------- keycaps (126)
// Wire index per key: position in the 14 2C 00 01 page stream IS the LED index.
const vKeys =
[
	0, 9, 18, 27, 36, 45, 54, 63, 72, 81, 90, 99, 108, 117, 114, 123, 1, 10, 19, 28,
	37, 46, 55, 64, 73, 82, 91, 100, 109, 87, 96, 105, 115, 6, 24, 16, 15, 2, 11, 20,
	29, 38, 47, 56, 65, 74, 83, 92, 101, 110, 119, 88, 97, 106, 61, 69, 70, 7, 3, 12,
	21, 30, 39, 48, 57, 66, 75, 84, 93, 102, 111, 120, 51, 52, 60, 4, 13, 22, 31, 40,
	49, 58, 67, 76, 85, 94, 103, 121, 124, 34, 42, 43, 33, 5, 14, 23, 32, 41, 50, 59,
	68, 77, 86, 95, 104, 113, 122, 78, 79
];

const vKeyNames =
[
	"Esc", "F1", "F2", "F3", "F4", "F5", "F6", "F7",
	"F8", "F9", "F10", "F11", "F12", "Print Screen", "Scroll Lock", "Pause Break",
	"`", "1", "2", "3", "4", "5", "6", "7",
	"8", "9", "0", "-_", "=+", "Backspace", "Insert", "Home",
	"Page Up", "NumLock", "Num /", "Num *", "Num -", "Tab", "Q", "W",
	"E", "R", "T", "Y", "U", "I", "O", "P",
	"[", "]", "\\", "Del", "End", "Page Down", "Num 7", "Num 8",
	"Num 9", "Num +", "CapsLock", "A", "S", "D", "F", "G",
	"H", "J", "K", "L", ";", "'", "ISO_#", "Enter",
	"Num 4", "Num 5", "Num 6", "Left Shift", "ISO_<", "Z", "X", "C",
	"V", "B", "N", "M", ",", ".", "/", "Right Shift",
	"Up Arrow", "Num 1", "Num 2", "Num 3", "Num Enter", "Left Ctrl", "Left Win", "Left Alt",
	"L-Space", "Space", "R-Space", "R-Underglow", "Right Alt", "Right Win", "Fn", "Right Ctrl",
	"Left Arrow", "Down Arrow", "Right Arrow", "Num 0", "Num ."
];

// Numpad on the right of the keyboard.
const vKeyPositionsRight =
[
	[1, 1], [2, 1], [3, 1], [4, 1], [5, 1], [7, 1], [8, 1], [9, 1], [10, 1], [11, 1],
	[12, 1], [13, 1], [14, 1], [15, 1], [16, 1], [17, 1], [1, 2], [2, 2], [3, 2], [4, 2],
	[5, 2], [6, 2], [7, 2], [8, 2], [9, 2], [10, 2], [11, 2], [12, 2], [13, 2], [14, 2],
	[15, 2], [16, 2], [17, 2], [21, 2], [22, 2], [23, 2], [24, 2], [1, 3], [2, 3], [3, 3],
	[4, 3], [5, 3], [6, 3], [7, 3], [8, 3], [9, 3], [10, 3], [11, 3], [12, 3], [13, 3],
	[14, 3], [15, 3], [16, 3], [17, 3], [21, 3], [22, 3], [23, 3], [24, 4], [1, 4], [2, 4],
	[3, 4], [4, 4], [5, 4], [6, 4], [7, 4], [8, 4], [9, 4], [10, 4], [11, 4], [12, 4],
	[13, 4], [14, 4], [21, 4], [22, 4], [23, 4], [1, 5], [2, 5], [3, 5], [4, 5], [5, 5],
	[6, 5], [7, 5], [8, 5], [9, 5], [10, 5], [11, 5], [12, 5], [14, 5], [16, 5], [21, 5],
	[22, 5], [23, 5], [24, 5], [1, 6], [2, 6], [3, 6], [4, 6], [7, 6], [9, 6], [10, 6],
	[11, 6], [12, 6], [13, 6], [14, 6], [15, 6], [16, 6], [17, 6], [21, 6], [23, 6]
];

// Numpad on the left of the keyboard.
const vKeyPositionsLeft =
[
	[8, 1], [9, 1], [10, 1], [11, 1], [12, 1], [14, 1], [15, 1], [16, 1], [17, 1], [18, 1],
	[19, 1], [20, 1], [21, 1], [22, 1], [23, 1], [24, 1], [8, 2], [9, 2], [10, 2], [11, 2],
	[12, 2], [13, 2], [14, 2], [15, 2], [16, 2], [17, 2], [18, 2], [19, 2], [20, 2], [21, 2],
	[22, 2], [23, 2], [24, 2], [1, 2], [2, 2], [3, 2], [4, 2], [8, 3], [9, 3], [10, 3],
	[11, 3], [12, 3], [13, 3], [14, 3], [15, 3], [16, 3], [17, 3], [18, 3], [19, 3], [20, 3],
	[21, 3], [22, 3], [23, 3], [24, 3], [1, 3], [2, 3], [3, 3], [4, 4], [8, 4], [9, 4],
	[10, 4], [11, 4], [12, 4], [13, 4], [14, 4], [15, 4], [16, 4], [17, 4], [18, 4], [19, 4],
	[20, 4], [21, 4], [1, 4], [2, 4], [3, 4], [8, 5], [9, 5], [10, 5], [11, 5], [12, 5],
	[13, 5], [14, 5], [15, 5], [16, 5], [17, 5], [18, 5], [19, 5], [21, 5], [23, 5], [1, 5],
	[2, 5], [3, 5], [4, 5], [8, 6], [9, 6], [10, 6], [11, 6], [14, 6], [16, 6], [17, 6],
	[18, 6], [19, 6], [20, 6], [21, 6], [22, 6], [23, 6], [24, 6], [1, 6], [3, 6]
];

// ---------------------------------------------------------------- border ring (45)
// Wire index per border LED, in physical clockwise order starting top-left
// (main board: top 11 -> right 4 -> bottom 12 -> left 4, then the numpad bezel:
// top 3 -> right 4 -> bottom 3 -> left 4). Capture-confirmed by K2.
const sideKeys =
[
	13, 14, 15, 7, 6, 5, 4, 3, 2, 1, 0, 9, 8, 10, 11, 12, 30, 29, 28, 27,
	26, 25, 24, 23, 22, 21, 20, 19, 18, 17, 16, 44, 43, 42, 41, 40, 39, 38, 37, 36,
	35, 34, 33, 32, 31
];

const sideNames =
[
	"Border Top 1", "Border Top 2", "Border Top 3", "Border Top 4", "Border Top 5",
	"Border Top 6", "Border Top 7", "Border Top 8", "Border Top 9", "Border Top 10",
	"Border Top 11", "Border Right 1", "Border Right 2", "Border Right 3", "Border Right 4",
	"Border Bottom 1", "Border Bottom 2", "Border Bottom 3", "Border Bottom 4", "Border Bottom 5",
	"Border Bottom 6", "Border Bottom 7", "Border Bottom 8", "Border Bottom 9", "Border Bottom 10",
	"Border Bottom 11", "Border Bottom 12", "Border Left 1", "Border Left 2", "Border Left 3",
	"Border Left 4", "Numpad Border Top 1", "Numpad Border Top 2", "Numpad Border Top 3", "Numpad Border Right 1",
	"Numpad Border Right 2", "Numpad Border Right 3", "Numpad Border Right 4", "Numpad Border Bottom 1", "Numpad Border Bottom 2",
	"Numpad Border Bottom 3", "Numpad Border Left 1", "Numpad Border Left 2", "Numpad Border Left 3", "Numpad Border Left 4"
];

const sidePositionsRight =
[
	[0, 0], [2, 0], [4, 0], [5, 0], [7, 0], [9, 0], [11, 0], [13, 0], [14, 0], [16, 0],
	[18, 0], [18, 1], [18, 3], [18, 4], [18, 6], [18, 7], [16, 7], [15, 7], [13, 7], [11, 7],
	[10, 7], [8, 7], [7, 7], [5, 7], [3, 7], [2, 7], [0, 7], [0, 6], [0, 4], [0, 3],
	[0, 1], [20, 0], [22, 0], [25, 0], [25, 1], [25, 3], [25, 4], [25, 6], [25, 7], [22, 7],
	[20, 7], [20, 6], [20, 4], [20, 3], [20, 1]
];

const sidePositionsLeft =
[
	[7, 0], [9, 0], [11, 0], [12, 0], [14, 0], [16, 0], [18, 0], [20, 0], [21, 0], [23, 0],
	[25, 0], [25, 1], [25, 3], [25, 4], [25, 6], [25, 7], [23, 7], [22, 7], [20, 7], [18, 7],
	[17, 7], [15, 7], [14, 7], [12, 7], [10, 7], [9, 7], [7, 7], [7, 6], [7, 4], [7, 3],
	[7, 1], [0, 0], [2, 0], [5, 0], [5, 1], [5, 3], [5, 4], [5, 6], [5, 7], [2, 7],
	[0, 7], [0, 6], [0, 4], [0, 3], [0, 1]
];

function keyPositions() { return layout === "Left" ? vKeyPositionsLeft : vKeyPositionsRight; }
function sidePositions() { return layout === "Left" ? sidePositionsLeft : sidePositionsRight; }

function allNames() { return borderLeds ? vKeyNames.concat(sideNames) : vKeyNames; }
function allPositions() { return borderLeds ? keyPositions().concat(sidePositions()) : keyPositions(); }

export function LedNames() { return vKeyNames.concat(sideNames); }
export function LedPositions() { return vKeyPositionsRight.concat(sidePositionsRight); }

// ---------------------------------------------------------------- device I/O

const KEYCAP_PAGES = 7;          // 7 x 19 slots = 133 (126 real keys + 7 padding)
const KEYCAP_SLOTS = KEYCAP_PAGES * 19;
const SIDE_CHUNKS = [19, 19, 7]; // 45 border LEDs

const ZONE_KEYCAPS = 0x02;
const ZONE_BORDER  = 0x05;

function brightnessByte() {
	const b = Math.round(Number(deviceBrightness));
	if (isNaN(b)) return 100;
	return Math.max(0, Math.min(100, b));
}

/** Per-packet pause. A full frame is 7 keycap pages + 3 border chunks, so this value
 *  times 10 is the floor on the frame time — 5ms means ~20fps, which reads as stutter. */
function packetPause() {
	const d = Math.round(Number(packetDelay));
	if (isNaN(d)) return 1;
	return Math.max(0, Math.min(10, d));
}

/** True when a[from..to) equals b[from..to) — used to skip re-sending unchanged pages. */
function sameSlice(a, b, from, to) {
	if (a.length !== b.length) return false;
	for (let i = from; i < to; i++) {
		if (a[i] !== b[i]) return false;
	}
	return true;
}

/** 11 01 00 <zone> 02 02 — puts a lighting zone into Custom mode. Base Camp sends this
 *  before every burst; once per (re)init is enough for a streaming plugin. The device
 *  answers with an echo burst, hence the read. */
function switchZone(zone) {
	device.write([0x00, 0x11, 0x01, 0x00, zone, 0x02, 0x02], 65);
	device.read([0x00], 65);
}

function enableCustom() {
	// 14 2C 0A 00 FF <brightness>, rest 0xFF — padding copied byte-for-byte from the capture.
	const pkt = [0x00, 0x14, 0x2C, 0x0A, 0x00, 0xFF, brightnessByte()];
	while (pkt.length < 65) pkt.push(0xFF);
	device.write(pkt, 65);
	device.read([0x00], 65);
}

function initDevice() {
	device.write([0x00, 0x14, 0x00, 0x00, 0x00, 0x01, 0x06], 65); // select lighting slot
	device.read([0x00], 65);
	enableCustom();
	switchZone(ZONE_KEYCAPS);
	if (borderLeds) switchZone(ZONE_BORDER);
}

function applyLayout() {
	device.setControllableLeds(allNames(), allPositions());
}

export function Initialize() {
	applyLayout();
	initDevice();
	oldKeycaps = [];
	oldBorder = [];
}

export function onlayoutChanged()           { applyLayout(); }
export function onborderLedsChanged()       { applyLayout(); oldBorder = []; if (borderLeds) switchZone(ZONE_BORDER); }
export function ondeviceBrightnessChanged() { enableCustom(); oldKeycaps = []; oldBorder = []; }

let savedResetTimer = Date.now();
const PollModeInterval = 300000;

export function Render() {
	if (Date.now() - savedResetTimer >= PollModeInterval) {
		savedResetTimer = Date.now();
		initDevice();   // the keyboard drops back to its stored effect if left alone
	}
	sendKeycaps();
	if (borderLeds) sendBorder();
}

export function Shutdown(SystemSuspending) {
	const color = SystemSuspending ? "#000000" : shutdownColor;
	sendKeycaps(color);
	if (borderLeds) sendBorder(color);
}

function colorFor(pos, overrideColor) {
	if (overrideColor) return hexToRgb(overrideColor);
	if (LightingMode === "Forced") return hexToRgb(forcedColor);
	return device.color(pos[0], pos[1]);
}

/** Builds an RGB byte buffer indexed by wire index. */
function grabColors(wireIndices, positions, slots, overrideColor) {
	const rgb = new Array(slots * 3).fill(0);
	for (let i = 0; i < wireIndices.length; i++) {
		const c = colorFor(positions[i], overrideColor);
		const o = wireIndices[i] * 3;
		rgb[o] = c[0]; rgb[o + 1] = c[1]; rgb[o + 2] = c[2];
	}
	return rgb;
}

let oldKeycaps = [];
let oldBorder = [];

// Both senders compare PER PAGE, not per frame. The all-or-nothing compare the bundled
// plugin uses means one changed key re-sends all 7 pages; most effects only touch part of
// the board per frame, so skipping untouched pages cuts the packets — and therefore the
// frame time, which is packets x packetPause() — without changing what you see.
function sendKeycaps(overrideColor) {
	const rgb = grabColors(vKeys, keyPositions(), KEYCAP_SLOTS, overrideColor);
	const bright = brightnessByte();
	const pause = packetPause();

	for (let page = 0; page < KEYCAP_PAGES; page++) {
		const from = page * 57;
		if (sameSlice(rgb, oldKeycaps, from, from + 57)) continue;
		device.write([0x00, 0x14, 0x2C, 0x00, 0x01, page, bright, 0x00].concat(rgb.slice(from, from + 57)), 65);
		if (pause > 0) device.pause(pause);
	}
	oldKeycaps = rgb;
}

function sendBorder(overrideColor) {
	const rgb = grabColors(sideKeys, sidePositions(), 45, overrideColor);
	const pause = packetPause();

	let offset = 0;
	for (let chunk = 0; chunk < SIDE_CHUNKS.length; chunk++) {
		const n = SIDE_CHUNKS[chunk] * 3;
		if (!sameSlice(rgb, oldBorder, offset, offset + n)) {
			// byte 5 is 0xFF here, not the brightness: the border ring takes its level from
			// the 14 2C 0A master packet (capture-confirmed — BC never varies this byte).
			device.write([0x00, 0x14, 0x2D, 0x0A, 0x00, chunk, 0xFF, 0x00].concat(rgb.slice(offset, offset + n)), 65);
			if (pause > 0) device.pause(pause);
		}
		offset += n;
	}
	oldBorder = rgb;
}

function hexToRgb(hex) {
	const result = /^#?([a-f\d]{2})([a-f\d]{2})([a-f\d]{2})$/i.exec(hex);
	if (!result) return [0, 0, 0];
	return [parseInt(result[1], 16), parseInt(result[2], 16), parseInt(result[3], 16)];
}

export function Validate(endpoint) {
	return endpoint.interface === 3;
}

export function ImageUrl() {
	return "https://assets.signalrgb.com/devices/brands/mountain/keyboards/everest-max.png";
}
