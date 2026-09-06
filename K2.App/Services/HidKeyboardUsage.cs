// Services/HidKeyboardUsage.cs — USB HID keyboard/keypad usage (page 0x07) -> Win32 virtual-key code.
//
// Needed because the Everest keyboard, while K2 is the foreground window, stops
// emitting standard keyboard input: its key presses reach K2 only as NKRO
// native HID reports (EverestService.KeyEvent, FromNativeKeyReport). A global
// WH_KEYBOARD_LL hook therefore sees nothing, so MacroRecorder cannot capture
// typing on the Everest that way — the Everest key events are fed into the
// recorder directly instead (MainWindow.Everest.cs -> MacroRecorder.InjectKey),
// and this table converts the HID usage they carry into the VK code the rest of
// the macro pipeline (playback via SendInput / SendKeys) expects.

namespace K2.App.Services;

internal static class HidKeyboardUsage
{
    /// <summary>Returns the Win32 VK code for a HID keyboard usage, or 0 if unmapped.</summary>
    public static int ToVirtualKey(int usage)
    {
        // Letters a..z  (0x04..0x1D) -> VK_A..VK_Z (0x41..0x5A)
        if (usage >= 0x04 && usage <= 0x1D) return 0x41 + (usage - 0x04);

        // Digits 1..9,0  (0x1E..0x27) -> '1'..'9' (0x31..0x39), '0' (0x30)
        if (usage >= 0x1E && usage <= 0x26) return 0x31 + (usage - 0x1E);
        if (usage == 0x27) return 0x30;

        // F1..F12 (0x3A..0x45) -> VK_F1..VK_F12 (0x70..0x7B)
        if (usage >= 0x3A && usage <= 0x45) return 0x70 + (usage - 0x3A);
        // F13..F24 (0x68..0x73) -> VK_F13..VK_F24 (0x7C..0x87)
        if (usage >= 0x68 && usage <= 0x73) return 0x7C + (usage - 0x68);

        // Keypad 1..9 (0x59..0x61) -> VK_NUMPAD1..9 (0x61..0x69); Keypad 0 (0x62) -> VK_NUMPAD0 (0x60)
        if (usage >= 0x59 && usage <= 0x61) return 0x61 + (usage - 0x59);
        if (usage == 0x62) return 0x60;

        return usage switch
        {
            0x28 => 0x0D, // Enter
            0x29 => 0x1B, // Esc
            0x2A => 0x08, // Backspace
            0x2B => 0x09, // Tab
            0x2C => 0x20, // Space
            0x2D => 0xBD, // - _   (VK_OEM_MINUS)
            0x2E => 0xBB, // = +   (VK_OEM_PLUS)
            0x2F => 0xDB, // [ {   (VK_OEM_4)
            0x30 => 0xDD, // ] }   (VK_OEM_6)
            0x31 => 0xDC, // \ |   (VK_OEM_5)
            0x32 => 0xDF, // non-US # ~ (VK_OEM_8)
            0x33 => 0xBA, // ; :   (VK_OEM_1)
            0x34 => 0xDE, // ' "   (VK_OEM_7)
            0x35 => 0xC0, // ` ~   (VK_OEM_3)
            0x36 => 0xBC, // , <   (VK_OEM_COMMA)
            0x37 => 0xBE, // . >   (VK_OEM_PERIOD)
            0x38 => 0xBF, // / ?   (VK_OEM_2)
            0x39 => 0x14, // Caps Lock
            0x46 => 0x2C, // PrintScreen
            0x47 => 0x91, // Scroll Lock
            0x48 => 0x13, // Pause
            0x49 => 0x2D, // Insert
            0x4A => 0x24, // Home
            0x4B => 0x21, // Page Up
            0x4C => 0x2E, // Delete
            0x4D => 0x23, // End
            0x4E => 0x22, // Page Down
            0x4F => 0x27, // Right
            0x50 => 0x25, // Left
            0x51 => 0x28, // Down
            0x52 => 0x26, // Up
            0x53 => 0x90, // Num Lock
            0x54 => 0x6F, // Keypad /
            0x55 => 0x6A, // Keypad *
            0x56 => 0x6D, // Keypad -
            0x57 => 0x6B, // Keypad +
            0x58 => 0x0D, // Keypad Enter
            0x63 => 0x6E, // Keypad .
            0x64 => 0xE2, // non-US \ |  (VK_OEM_102)
            0x65 => 0x5D, // Application (menu)
            0xE0 => 0xA2, // Left Ctrl
            0xE1 => 0xA0, // Left Shift
            0xE2 => 0xA4, // Left Alt
            0xE3 => 0x5B, // Left GUI / Win
            0xE4 => 0xA3, // Right Ctrl
            0xE5 => 0xA1, // Right Shift
            0xE6 => 0xA5, // Right Alt
            0xE7 => 0x5C, // Right GUI / Win
            _    => 0
        };
    }
}
