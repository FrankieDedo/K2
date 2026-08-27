using System.Collections.Generic;

namespace K2.App.Models;

/// <summary>One labeled sub-section of the Settings tab's "Extra" link list (see
/// MainWindow.Settings.cs's InitExtraLinksPanel), e.g. "3D printing" / "Other projects".</summary>
public sealed class ExtraLinkGroup
{
    public ExtraLinkGroup(string title, IReadOnlyList<ExtraLinkItem> items)
    {
        Title = title;
        Items = items;
    }

    public string Title { get; }
    public IReadOnlyList<ExtraLinkItem> Items { get; }
}
