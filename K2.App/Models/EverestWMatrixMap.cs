using System.Collections.Generic;

namespace K2.App.Models;

/// <summary>
/// Default Everest Max wMatrix (SDK KEY_CALLBACK code / Base Camp DB's DLLMatrixIndex) →
/// matrixId (Windows VK code, see <see cref="KeyDef.MatrixId"/>) translation table for
/// regular keyboard keys. Derived from a real BaseCamp.db EverestKeyBidings dump.
///
/// Shared by MainWindow.Everest.cs (translating live SDK key-press callbacks via
/// EvTranslateMatrix) and <see cref="K2.App.Services.BaseCampDbImporter"/> (translating
/// imported DLLMatrixIndex bindings into that same VK-code space before they're saved as
/// EverestKeyRecord.KeyMatrix) — the two MUST agree, since a manually-created key (via the
/// keyboard overlay click, whose Tag is already a VK-code MatrixId) and an imported key are
/// looked up from the very same <c>_evByMatrix</c> dictionary on a physical key press.
/// Before this shared table existed, the importer wrote the raw wMatrix straight into
/// KeyMatrix — a different numbering space from the VK codes translated live presses land
/// on, so an imported key could never be found by a physical press (confirmed user report
/// 2026-07-19: "l'azione non funziona" after import).
/// </summary>
public static class EverestWMatrixMap
{
    public static readonly IReadOnlyDictionary<int, int> Default = new Dictionary<int, int>
    {
        {   0,  27 },  // Esc
        {   2,   9 },  // Tab
        {   3,  20 },  // Caps Lk
        {   4, 160 },  // LShift
        {   5, 162 },  // LCtrl
        {   6, 144 },  // Num Lk
        {   7, 107 },  // Num +
        {   9, 112 },  // F1
        {  10,  49 },  // 1
        {  11,  81 },  // Q
        {  12,  65 },  // A
        {  13, 226 },  // < (ISO extra key)
        {  14,  91 },  // Win
        {  15, 109 },  // Num -
        {  16, 106 },  // Num *
        {  18, 113 },  // F2
        {  19,  50 },  // 2
        {  20,  87 },  // W
        {  21,  83 },  // S
        {  22,  90 },  // Z
        {  23,  18 },  // Alt
        {  24, 111 },  // Num /
        {  27, 114 },  // F3
        {  28,  51 },  // 3
        {  29,  69 },  // E
        {  30,  68 },  // D
        {  31,  88 },  // X
        {  33,  13 },  // Num Enter
        {  34,  97 },  // Num 1
        {  35, 173 },  // Mute
        {  36, 115 },  // F4
        {  37,  52 },  // 4
        {  38,  82 },  // R
        {  39,  70 },  // F
        {  40,  67 },  // C
        {  41,  32 },  // Space
        {  42,  98 },  // Num 2
        {  43,  99 },  // Num 3
        {  45, 116 },  // F5
        {  46,  53 },  // 5
        {  47,  84 },  // T
        {  48,  71 },  // G
        {  49,  86 },  // V
        {  51, 100 },  // Num 4
        {  52, 101 },  // Num 5
        {  53, 177 },  // Prev Track
        {  54, 117 },  // F6
        {  55,  54 },  // 6
        {  56,  89 },  // Y
        {  57,  72 },  // H
        {  58,  66 },  // B
        {  60, 102 },  // Num 6
        {  61, 103 },  // Num 7
        {  62, 176 },  // Next Track
        {  63, 118 },  // F7
        {  64,  55 },  // 7
        {  65,  85 },  // U
        {  66,  74 },  // J
        {  67,  78 },  // N
        {  68, 165 },  // Alt Gr (VK_RMENU)
        {  69, 104 },  // Num 8
        {  70, 105 },  // Num 9
        {  72, 119 },  // F8
        {  73,  56 },  // 8
        {  74,  73 },  // I
        {  76,  77 },  // M
        {  77,  91 },  // Win (right)
        {  78,  96 },  // Num 0
        {  79, 110 },  // Num .
        {  81, 120 },  // F9
        {  82,  57 },  // 9
        {  83,  79 },  // O
        {  84,  76 },  // L
        {  85, 188 },  // ,
        {  87,   8 },  // Backspace
        {  88,  46 },  // Del
        {  90, 121 },  // F10
        {  91,  48 },  // 0
        {  92,  80 },  // P
        {  93, 222 },  // ò
        {  94, 190 },  // .
        {  95, 163 },  // RCtrl
        {  96,  45 },  // Insert
        {  97,  35 },  // End
        {  99, 122 },  // F11
        { 101, 186 },  // è
        { 102, 192 },  // à
        { 103, 189 },  // -
        { 104,  37 },  // ←
        { 105,  36 },  // Home
        { 106,  34 },  // PgDn
        { 108, 123 },  // F12
        { 110, 187 },  // +
        { 111, 219 },  // ù
        { 113,  40 },  // ↓
        { 114, 145 },  // Scroll Lk
        { 115,  33 },  // PgUp
        { 117,  44 },  // Prt Sc
        { 120,  13 },  // Enter  ← wMatrix=120 = Enter's DLLMatrixIndex
        { 121, 161 },  // RShift
        { 122,  39 },  // →
        { 123,  19 },  // Pause
        { 124,  38 },  // ↑
        { 183, 179 },  // Play/Pause
    };

    /// <summary>Translates a wMatrix/DLLMatrixIndex to its VK matrixId via <see cref="Default"/>,
    /// falling back to the input unchanged when there's no known translation (e.g.
    /// programmable/dock/media-dock keys, which go through the separate HW-capture mechanism
    /// instead — see MainWindow.DockActions.cs).</summary>
    public static int Translate(int wMatrix) =>
        Default.TryGetValue(wMatrix, out var vk) ? vk : wMatrix;
}
