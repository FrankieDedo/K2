// LocExtension.cs — XAML markup extension for localized strings.
// Usage: xmlns:loc="clr-namespace:K2.Core;assembly=K2.Core"
//        Header="{loc:Get tab_everest}"
// The value is resolved once at XAML load time, so it picks up whatever
// language is active (from K2.lang / K2_LANG / UI culture).
using System;
using System.Windows.Markup;

namespace K2.Core;

/// <summary>
/// XAML markup extension that returns a localized string via <see cref="Loc.Get"/>.
/// Resolved once at load time — restart the app to apply a language change.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class GetExtension : MarkupExtension
{
    /// <summary>The localization key (e.g. "tab_everest").</summary>
    public string Key { get; set; } = "";

    public GetExtension() { }
    public GetExtension(string key) => Key = key;

    public override object ProvideValue(IServiceProvider serviceProvider)
        => Loc.Get(Key);
}
