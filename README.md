K2 is an unofficial, full recreation of Base Camp, the management software for every device made by the now defunct company, Mountain. 

First of all, i have to thank ramisotti, because their own project (https://github.com/ramisotti13-eng/BaseCamp-Linux) helped me in building some of the communication protocol for K2. This was very important and it would have been a mess to build it from scratch.

This was more of a learning project for me and... it's working! I wanted to learn more about Claude Code, C# and USB protocols, and in the meantime, i tried to build something useful for me and everybody else that use these pieces of hardware!

The project is still in a beta stage, but most of the stuff should work alright. I think that there is still some streamlining and optimization to do, but i should iron out everything soon. Anyway, here are the features implemented as of now:

<h1>K2 — Feature Overview</h1>

<details open>
<summary><strong>General/shared settings</strong></summary>
<ul>
  <li><strong>Home tab</strong>: tile grid, one card per connected device, always the first tab</li>
  <li><strong>Macro</strong>: recording (keyboard+mouse), library, playback (once/repeat/while-held/toggle), assignable as an action on any key of any device</li>
  <li><strong>Assignable action types</strong>: keyboard shortcut, launch program (with icon), open folder, open browser (detects installed Chrome/Edge/Firefox/Opera/Brave), OS command, media keys, mouse actions, play macro, cross-device profile switch, Python script (with an HTTP bridge to K2)</li>
  <li><strong>Profile import</strong> from Base Camp's database/XML</li>
  <li><strong>Shared dark theme</strong>, <strong>selectable font</strong> (8 options including OpenDyslexic for accessibility) with adjustable size</li>
  <li><strong>Drag &amp; drop</strong> to swap action+icon between two keys (even across different devices)</li>
  <li><strong>Windows installer</strong> (Inno Setup, self-contained) + portable ZIP package</li>
  <li><strong>Profiles</strong> Supports Base Camp XMLs and direct import from Base Camp database</li>
  <li><strong>Macros</strong> Full mapping interface and supports importing from Base Camp</li>
</ul>
</details>

<details open>
<summary><strong>Mappable key actions</strong></summary>
<ul>
  <li><strong>Keyboard shortcut</strong> — any combo of modifiers (Ctrl/Shift/Alt/Win) + key</li>
  <li><strong>Launch program</strong> — path to an .exe, with icon and a recent-items list</li>
  <li><strong>Open folder</strong> — path to a folder, with a recent-items list</li>
  <li><strong>Open browser</strong> — pick from the browsers detected on the system (Chrome/Edge/Firefox/Opera/Brave)</li>
  <li><strong>OS command</strong> — built-in system actions (e.g. lock, sleep, volume)</li>
  <li><strong>Media keys</strong> — play/pause, next/previous track, volume up/down/mute</li>
  <li><strong>Mouse action</strong> — click, scroll, and other mouse-emulated inputs</li>
  <li><strong>Switch profile</strong> — change profile on this device or target a different connected device</li>
  <li><strong>Play macro</strong> — trigger any recorded macro (keyboard+mouse) from the shared library</li>
  <li><strong>Python script</strong> — run an inline snippet or a .py file, with an HTTP bridge back into K2 (log, read state, switch profile, run another action, simulate a key press)</li>
</ul>
<p><em>Available on every device — MacroPad, Everest Max/60, DisplayPad — through the same "Configure action" dialog.</em></p>
</details>

<details open>
<summary><strong>Everest Core/Max</strong></summary>
<ul>
  <li>Key assignment</li>
  <li>Keycap and keyboard customization: You can put in the UI the actual style of your keyboard, with Normal/Pudding/ReversePudding keycaps, including translucent legends and you can choose between silver and black frames. Also, you can put any image on there, to emulate custom keycaps.</li>
  <li>RGB lighting: 8 presets (Static/Breath/Wave/Reactive A-B-C/Yeti/Tornado/Matrix/Off), speed, direction CW/CCW, brightness, 3 color pickers, cross-profile sync, backlight on/off, reset</li>
  <li>Custom per-key lighting (paint mode)</li>
  <li>Numpad display keys: upload a custom image to each of the 4 keys + assignable action</li>
  <li>Media Dock: clock, CPU/RAM monitoring, LED bar effects, screensaver, reset</li>
  <li>Display Dial: 8 pages (12h/24h format, analog/digital style, screensaver/auto-off)</li>
  <li>Dynamic layout (dock/numpad reposition based on what's attached)</li>
  <li>USB Recorder (diagnostic tool for comparing captures)</li>
</ul>
</details>

<details open>
<summary><strong>Everest 60</strong></summary>
<ul>
  <li>Key assignment</li>
  <li>Connection detection + accessory numpad with auto-detected side (left/right)</li>
  <li>RGB lighting (same presets as Everest Max) + 44-LED side ring + per-key Key Lighting with live preview</li>
  <li>Keycap and keyboard customization: You can put in the UI the actual style of your keyboard, with Normal/Pudding/ReversePudding keycaps, including translucent legends. Also, you can put any image on there, to emulate custom keycaps.</li>
</ul>
<p><em>Not yet verified on physical hardware — functionally complete but untested on a real device.</em></p>
</details>

<details open>
<summary><strong>Makalu 67/Max</strong></summary>
<ul>
  <li>RGB lighting (presets + custom 8-LED editor) with ring preview</li>
  <li>DPI management</li>
  <li>Button remap including sniper button</li>
  <li>Device settings: polling rate, debounce, lift-off, angle snapping</li>
  <li>Device image with clickable hotspots for selecting the button to remap</li>
</ul>
</details>

<details open>
<summary><strong>DisplayPad</strong></summary>
<ul>
  <li>Key capture and action assignment</li>
  <li>Set a device rotation 90°/270° (if you keep them vertical, the software will behave accordingly)</li>
  <li>More than two DisplayPads at once are supported</li>
  <li>Fullscreen images are supported (only as still picture for now) and GIFs as well, but performance is abysmal and only works good with low framerate GIFs, because of Displaypad's hardware specifications, probably unfixable</li>
</ul>
</details>

<details open>
<summary><strong>MacroPad</strong></summary>
<ul>
  <li>Key capture and action assignment</li>
  <li>LED lighting panel: effect preset, speed, direction, 3 color pickers, brightness, cross-profile sync, backlight on/off, reset, save to flash</li>
  <li>Set a device rotation 90°/270° (if you keep them vertical, the software will behave accordingly)</li>
  <li>Keycap and keyboard customization: You can put in the UI the actual style of your keyboard, with Normal/Pudding/ReversePudding keycaps, including translucent legends. Also, you can put any image on there, to emulate custom keycaps.</li>
</ul>
</details>

<h1>What's coming</h1>
<ul>
  <li>More accessibility options (and functions, maybe)</li>
  <li>Driving wheel mode for DisplayPad — turning it into an interactive display for racing sims, and possibly other sims down the line (flight sims?)</li>
  <li>SignalRGB support</li>
  <li>Accurate LED sync between devices</li>
  <li>Desk layout feature — for users with multiple Mountain accessories, track where each device sits on your desk and drive multi-device LED effects across them (similar to Razer-style software)</li>
  <li>More DisplayPad shenanigans</li>
</ul>

<a href='https://ko-fi.com/Q6J7237TZX' target='_blank'><img height='36' style='border:0px;height:36px;' src='https://storage.ko-fi.com/cdn/kofi6.png?v=6' border='0' alt='Buy Me a Coffee at ko-fi.com' /></a>
