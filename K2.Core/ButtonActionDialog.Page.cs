using System.Linq;
using System.Windows.Controls;

namespace K2.Core;

/// <summary>
/// "Page" action type (Tag="dp_folder"): lets a key navigate into a DisplayPad sub-page,
/// either an existing one (picked from <see cref="IActionHost.ListPages"/>, with its
/// current name editable — i.e. renameable right here) or a brand-new one (typed name,
/// created via <see cref="IActionHost.CreatePage"/>). Previously this action type could
/// only ever be created via the DisplayPad grid's "Create folder" context menu item —
/// this is the same action, just also reachable/editable from the normal "Configure
/// action" dialog, same as every other action type.
/// </summary>
public partial class ButtonActionDialog
{
    private const string NewPageTag = "__new__";

    /// <summary>Page ID this dialog was opened with (null = no page assigned yet).</summary>
    private int? _originalPageId;
    /// <summary>That page's name at the time the dialog opened (for change detection).</summary>
    private string? _originalPageName;

    /// <summary>
    /// True after <see cref="SavePageSpec"/> if the icon's caption needs regenerating —
    /// creating a new page, switching to a different existing page, or renaming the
    /// current one all count, even when <see cref="ActionValue"/> (the page ID) ends up
    /// unchanged (a plain rename), which callers can't detect from
    /// old-value/new-value comparison alone.
    /// </summary>
    public bool PageIconNeedsRefresh { get; private set; }

    /// <summary>Resolved page name after <see cref="SavePageSpec"/> — the caption to bake
    /// into the icon via <c>IconImageGenerator.TryGenerateFolderIcon</c>.</summary>
    public string? ResolvedPageName { get; private set; }

    /// <summary>Populates <see cref="CbPage"/> once: a "new page" option first, then every
    /// existing page from <see cref="IActionHost.ListPages"/> — pre-selecting/pre-filling
    /// the one this dialog was opened with, if any (see <see cref="_originalPageId"/>).
    /// </summary>
    private void EnsurePagePanel()
    {
        if (CbPage.Items.Count > 0) return;

        var newItem = new ComboBoxItem { Content = Loc.Get("page_new_option"), Tag = NewPageTag };
        CbPage.Items.Add(newItem);

        var pages = _host?.ListPages() ?? System.Array.Empty<(int PageId, string Name)>();
        foreach (var (pageId, name) in pages)
            CbPage.Items.Add(new ComboBoxItem { Content = name, Tag = pageId.ToString() });

        if (_originalPageId is int pid)
        {
            var match = CbPage.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => (string?)i.Tag == pid.ToString());
            if (match is not null)
            {
                CbPage.SelectedItem = match;
                _originalPageName = match.Content as string;
                TxtPageName.Text = _originalPageName ?? "";
                return;
            }
        }

        CbPage.SelectedItem = newItem;
        TxtPageName.Text = "";
    }

    private void CbPage_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CbPage.SelectedItem is not ComboBoxItem ci) return;
        TxtPageName.Text = (string?)ci.Tag == NewPageTag ? "" : ci.Content as string ?? "";
    }

    private string SavePageSpec()
    {
        string name = TxtPageName.Text?.Trim() ?? "";
        if (name.Length == 0) name = "Page";

        bool isNew = CbPage.SelectedItem is ComboBoxItem { Tag: NewPageTag };
        if (isNew)
        {
            int pageId = _host?.CreatePage(name) ?? 0;
            ResolvedPageName = name;
            PageIconNeedsRefresh = true;
            return pageId.ToString();
        }

        int existingId = CbPage.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out int pid)
            ? pid : _originalPageId ?? 0;
        if (existingId != _originalPageId || name != _originalPageName)
        {
            _host?.RenamePage(existingId, name);
            PageIconNeedsRefresh = true;
        }
        ResolvedPageName = name;
        return existingId.ToString();
    }
}
