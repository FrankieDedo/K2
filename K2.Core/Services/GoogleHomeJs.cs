namespace K2.Core.Services;

/// <summary>
/// Shared JS helpers injected into every home.google.com document (both the hidden trigger
/// view in <see cref="GoogleHomeBridge"/> and the visible one in
/// <see cref="GoogleHomeSetupWindow"/>) via <c>AddScriptToExecuteOnDocumentCreatedAsync</c>,
/// so they're available again after every navigation without re-sending the same JS text on
/// each call.
///
/// Confirmed against two real saved home.google.com pages (Automations list AND Devices
/// grid — an Angular Material app, "ghw-*" component tags but NOT native Shadow DOM):
/// repeated entries always render as a small card wrapping BOTH an identifying name element
/// (a descendant whose class name contains "name" or "title" — ".automation-name" on the
/// Automations page, ".title" on the Devices page) AND an action control (a button/
/// role="button") whose own accessible name (aria-label, or "title" attribute — Devices tiles
/// use <c>title="Attiva"</c>/"Disattiva", not aria-label) is the SAME generic string on every
/// card of that kind ("Avvia automazione" for every routine, "Attiva" for every inactive
/// light). A binding therefore always needs (card name + control), never the control alone.
///
/// On the Devices page the name element sits INSIDE the control itself (the whole tile is
/// one big button); on the Automations page it's a SIBLING of the control, several ancestors
/// up (inside the shared mat-card).
///
/// <see cref="Helpers"/>' <c>findCardFor</c> originally found the card by walking upward from
/// the control until SOME ancestor "had a name" — too permissive: a device's own name is ALSO
/// repeated verbatim as a room-picker sidebar entry (<c>&lt;mat-sidenav role="navigation"&gt;</c>,
/// confirmed real markup — icon <c>meeting_room</c> + the room's name, and a room can share a
/// device's exact name, e.g. room "Studio" containing a device also named "Studio"), and the
/// generic upward walk from ANY button on the page — including sidebar entries, found first
/// since the sidebar renders before the main content — matched those just as happily as a real
/// device tile. Clicking a room-picker entry just navigates/expands the room: a real,
/// successful click (K2 logged "ok"), never touching the actual device. <c>findCardFor</c> now
/// requires an EXPLICIT, confirmed container instead of any ancestor: <c>.device-tile</c> on
/// the Devices page, <c>[role="listitem"]</c> on the Automations page — neither ever matches
/// navigation chrome, a room's own `&lt;h2&gt;` section header, or the Automations page's own
/// "+ Aggiungi nuova" button.
///
/// Rooms group devices as <c>&lt;div class="space"&gt;&lt;h2&gt;RoomName&lt;/h2&gt;
/// &lt;div class="tile-container"&gt;...device tiles...&lt;/div&gt;&lt;/div&gt;</c> (confirmed
/// real markup) — <c>roomNameFor</c>/<c>displayNameFor</c> build a "Room / Device" name from
/// it, ALWAYS room-prefixed (even when the room and device happen to share the same text,
/// e.g. room "Studio" containing a device also named "Studio" — collapsing that away would
/// silently reintroduce ambiguity against a differently-roomed same-named device). This
/// qualified name is not just a display label — <c>scanCards</c>/<c>findCard</c> use it as the
/// actual dedup/match key (<c>GoogleHomeBinding.CardText</c>), since two different rooms can
/// each contain a same-named device and the plain device name alone cannot tell them apart.
/// </summary>
internal static class GoogleHomeJs
{
    public const string Helpers = """
        window.__k2gh = {
            findNameIn: function(root) {
                var nameEl = root.querySelector('[class*="name"], [class*="title"]');
                if (!nameEl) return '';
                var text = nameEl.textContent || nameEl.getAttribute('aria-label') || '';
                return text.replace(/\s+/g, ' ').trim();
            },
            controlLabelOf: function(control) {
                return control.getAttribute('aria-label') || control.getAttribute('title') || (control.textContent || '').trim();
            },
            inNavigation: function(el) {
                return !!(el && el.closest && el.closest('[role="navigation"]'));
            },
            roomNameFor: function(card) {
                var node = card;
                while (node && node !== document.body) {
                    if (node.classList && node.classList.contains('space')) {
                        var h2 = node.querySelector('h2');
                        if (h2) return (h2.textContent || '').replace(/\s+/g, ' ').trim();
                    }
                    node = node.parentElement;
                }
                return '';
            },
            // Always "Room / Device", even when they're literally the same string (e.g. a
            // room "Studio" containing a device also named "Studio") — the point isn't just
            // readability, it's that two DIFFERENT rooms can each contain a same-named device
            // ("Studio / Studio" vs "Salotto / Studio"), and collapsing the room away for a
            // same-as-room-name device would silently reintroduce that ambiguity for the user.
            displayNameFor: function(card) {
                var room = window.__k2gh.roomNameFor(card);
                var name = window.__k2gh.findNameIn(card);
                return room ? (room + ' / ' + name) : name;
            },
            iconNameFor: function(card) {
                var icon = card.querySelector('mat-icon');
                return icon ? (icon.textContent || '').replace(/\s+/g, ' ').trim() : '';
            },
            nearestControl: function(el) {
                var node = el;
                var hops = 0;
                while (node && node !== document.body && hops < 8) {
                    if (node.tagName === 'BUTTON' || (node.getAttribute && node.getAttribute('role') === 'button')) return node;
                    node = node.parentElement;
                    hops++;
                }
                return el;
            },
            // Explicit, confirmed container only (see class doc for why the earlier generic
            // upward walk was too permissive) — never matches navigation chrome, a room's own
            // <h2> header, or a page-level button like Automations' "+ Aggiungi nuova".
            findCardFor: function(control) {
                if (window.__k2gh.inNavigation(control)) return null;
                if (!control.closest) return null;
                var container = control.closest('.device-tile, [role="listitem"]');
                if (!container) return null;
                var name = window.__k2gh.findNameIn(container);
                return name ? container : null;
            },
            // label may be stale: a device tile's own aria-label/title often encodes CURRENT
            // STATE (e.g. "Attiva"/"Disattiva" depending on on/off) rather than a stable name,
            // so it can legitimately no longer match the string captured earlier — that is
            // NOT the same as "the control disappeared". If nothing matches AND scope itself
            // is directly clickable (the confirmed device-tile shape, where the card IS the
            // control — see class doc), click scope itself as the last resort rather than
            // reporting not-found for what is really just a flipped toggle state.
            findControlLike: function(scope, label) {
                function isClickable(el) {
                    return !!el && (el.tagName === 'BUTTON' || (el.getAttribute && el.getAttribute('role') === 'button'));
                }
                function matches(el) {
                    if (!el || !el.getAttribute) return false;
                    return el.getAttribute('aria-label') === label || el.getAttribute('title') === label
                        || (el.textContent || '').trim() === label;
                }
                if (matches(scope)) return scope;
                var all = scope.querySelectorAll('*');
                for (var i = 0; i < all.length; i++) { if (matches(all[i])) return all[i]; }
                var buttons = scope.querySelectorAll('button, [role="button"]');
                if (buttons.length > 0) return buttons[0];
                if (isClickable(scope)) return scope;
                return null;
            },
            // Page-wide search by card name (trigger side, only the name is known). MUST use
            // the exact same button-driven walk as findCardFor/scanCards — an earlier version
            // picked the smallest element anywhere whose subtree contained a name match, which
            // degenerates to the tightest wrapper around the name text itself (e.g. a device
            // tile's inner ".content" div, which is SMALLER than the actual toggle <button>
            // that contains both the icon and that div) — never clickable and with no button
            // inside it either, so every trigger silently failed. Walking from real buttons
            // instead guarantees the returned card is always the same element capture/scanCards
            // already validated as being at-or-above a real control. Matches on displayNameFor
            // (room-qualified), not the raw device name — the stored/matched "card text" IS the
            // qualified name (see scanCards), so two different rooms' same-named devices never
            // collide here either.
            findCard: function(text) {
                var buttons = document.querySelectorAll('button, [role="button"]');
                for (var i = 0; i < buttons.length; i++) {
                    var card = window.__k2gh.findCardFor(buttons[i]);
                    if (card && window.__k2gh.displayNameFor(card) === text) return card;
                }
                return null;
            },
            // Plain el.click() only fires a synthetic 'click' event (isTrusted: false) with no
            // pointer/mouse events at all — confirmed insufficient against a real device: K2
            // reliably finds and "clicks" the correct on-off tile, Angular even visibly reacts,
            // yet the physical light never toggles even with a single well-spaced press and a
            // multi-second wait. Material's interaction handling (ripple, focus, the actual
            // command dispatch) is commonly wired to the full pointer/mouse gesture, not just a
            // bare click event, so this fires the whole sequence a real interaction produces
            // (pointerover/enter/down, mousedown, pointerup, mouseup, click) with real
            // coordinates from the element's own bounding box. Still synthetic (isTrusted stays
            // false — that's a browser-level guarantee JS can't spoof), but exercises far more
            // of the code path than el.click() alone.
            simulateClick: function(el) {
                var rect = el.getBoundingClientRect();
                var x = rect.left + rect.width / 2;
                var y = rect.top + rect.height / 2;
                var base = {
                    bubbles: true, cancelable: true, composed: true, view: window,
                    clientX: x, clientY: y, button: 0, buttons: 1
                };
                var pointerBase = Object.assign({ pointerId: 1, pointerType: 'mouse', isPrimary: true }, base);
                function fire(type, ctor, opts) {
                    try { el.dispatchEvent(new ctor(type, opts)); } catch (e) { /* older engine: skip that event type */ }
                }
                fire('pointerover', PointerEvent, pointerBase);
                fire('pointerenter', PointerEvent, pointerBase);
                fire('mouseover', MouseEvent, base);
                fire('mouseenter', MouseEvent, base);
                fire('pointerdown', PointerEvent, pointerBase);
                fire('mousedown', MouseEvent, base);
                if (el.focus) { try { el.focus(); } catch (e) {} }
                fire('pointerup', PointerEvent, pointerBase);
                fire('mouseup', MouseEvent, base);
                fire('click', MouseEvent, base);
            },
            // Rasterizes Material icon ligature names to PNG data URLs using the icon font the
            // page ITSELF already has loaded, read off a real <mat-icon>'s computed style. That
            // sidesteps what blocked this earlier: rendering these glyphs from Segoe MDL2/Fluent
            // would mean hardcoding codepoints, and a wrong guess renders a silently blank or
            // plain-wrong tile. Here the glyph is by construction the same one the user sees on
            // home.google.com.
            //
            // Deliberately NOT async (no await on document.fonts.ready): ExecuteScriptAsync
            // cannot await a promise, it would serialize the Promise object instead. The font is
            // loaded in practice by the time anything is scannable, and the width check below
            // catches the case where it isn't.
            renderIcons: function(names, px) {
                var sample = document.querySelector('mat-icon');
                var family = sample ? window.getComputedStyle(sample).fontFamily
                                    : '"Material Symbols Outlined","Material Icons"';
                var out = {};
                for (var i = 0; i < names.length; i++) {
                    var name = names[i];
                    if (!name) continue;
                    var canvas = document.createElement('canvas');
                    canvas.width = px;
                    canvas.height = px;
                    var ctx = canvas.getContext('2d');
                    ctx.font = px + 'px ' + family;
                    ctx.textAlign = 'center';
                    ctx.textBaseline = 'middle';
                    ctx.fillStyle = '#ffffff';
                    // Ligature check. An icon font renders "lightbulb" as ONE roughly square
                    // glyph (~1em wide); if the ligature did not apply — font not loaded yet, or
                    // a fallback font substituted — we would silently get the literal WORD
                    // instead, which is several times wider. Measuring is far more reliable than
                    // trying to spot the difference in the rendered pixels, and skipping here
                    // makes K2 fall back to a caption-only tile rather than shipping a tile with
                    // "lightbulb" written across it.
                    if (ctx.measureText(name).width > px * 1.6) continue;
                    ctx.fillText(name, px / 2, px / 2);
                    out[name] = canvas.toDataURL('image/png');
                }
                return JSON.stringify(out);
            },
            describe: function(el) {
                if (!el || el.nodeType !== 1) return null;
                return {
                    tag: el.tagName,
                    role: el.getAttribute ? el.getAttribute('role') : null,
                    ariaLabel: el.getAttribute ? el.getAttribute('aria-label') : null,
                    title: el.getAttribute ? el.getAttribute('title') : null,
                    text: (el.textContent || '').replace(/\s+/g, ' ').trim().slice(0, 80)
                };
            },
            // Dedup key MUST be the room-qualified name, not the raw device name — two
            // different rooms can each have a device with the same plain name, and deduping
            // on the plain name would silently drop every same-named device but the first.
            scanCards: function() {
                var buttons = document.querySelectorAll('button, [role="button"]');
                var out = [], seen = {};
                for (var i = 0; i < buttons.length; i++) {
                    var control = buttons[i];
                    var card = window.__k2gh.findCardFor(control);
                    if (!card) continue;
                    var qualified = window.__k2gh.displayNameFor(card);
                    if (!qualified || seen[qualified]) continue;
                    seen[qualified] = true;
                    out.push({
                        cardText: qualified,
                        controlLabel: window.__k2gh.controlLabelOf(control),
                        iconName: window.__k2gh.iconNameFor(card)
                    });
                }
                return JSON.stringify(out);
            }
        };
        """;
}
