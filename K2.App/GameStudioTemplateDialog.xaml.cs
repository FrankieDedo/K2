using System;
using System.Windows;
using K2.Core;

namespace K2.App;

/// <summary>What a new studio tile starts as. Every model but <see cref="Blank"/> lands a reading
/// AND the elements that show it, already pointed at each other — see
/// <c>GameStudioWindow.BuildFromTemplate</c>.</summary>
public enum StudioTemplate
{
    /// <summary>A bar filled from a 0..100% reading, with the value over it.</summary>
    Bar,

    /// <summary>A number read off the screen as text (OCR).</summary>
    Number,

    /// <summary>Two states, using the on and off halves of the tile's style.</summary>
    OnOff,

    /// <summary>The rectangle itself, copied onto the key.</summary>
    Mirror,

    /// <summary>What the studio always made before the models existed: one empty reading and the
    /// two pieces that make it readable.</summary>
    Blank,
}

/// <summary>
/// The "new action" popup: four models and a way out of them.
///
/// <para>A tile is a reading plus a handful of elements pointing at it, and having to assemble that
/// vocabulary before seeing anything on the key is the step people gave up on. Each card here is a
/// finished starting point, so the first thing the user does after it is the only thing that is
/// really theirs: showing K2 where on the screen to look.</para>
/// </summary>
public partial class GameStudioTemplateDialog : Window
{
    /// <summary>The model picked, or null when the popup was cancelled.</summary>
    public StudioTemplate? Choice { get; private set; }

    public GameStudioTemplateDialog(string profileName)
    {
        InitializeComponent();
        TxtTitle.Text = profileName.Length > 0
            ? Loc.Get("studio_tpl_for_fmt", profileName)
            : Loc.Get("studio_tpl_title_none");
    }

    private void Card_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag } ||
            !Enum.TryParse(tag, out StudioTemplate template)) return;
        Pick(template);
    }

    private void Blank_Click(object sender, RoutedEventArgs e) => Pick(StudioTemplate.Blank);

    private void Pick(StudioTemplate template)
    {
        Choice = template;
        DialogResult = true;
    }
}
