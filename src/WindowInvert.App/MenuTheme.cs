namespace WindowInvert.App;

/// <summary>
/// Paints the tray menu to match the user's Windows colour setting.
/// <para>
/// WinForms does not do this on its own. A <c>ContextMenuStrip</c> renders with
/// the light professional palette regardless of the system theme, so on a
/// dark-themed desktop this application's only piece of ordinary UI arrived as
/// a bright white rectangle - which is a poor look for anything, and a
/// particularly poor one for a tool whose entire purpose is controlling how
/// bright a window is.
/// </para>
/// <para>
/// .NET 9 added <c>Application.SetColorMode</c>, which does this centrally.
/// This project targets .NET 8, so the palette is supplied by hand. If the
/// target framework moves, this whole file is replaceable by that one call.
/// </para>
/// </summary>
internal static class MenuTheme
{
    /// <summary>
    /// Applies the current system theme to every WinForms menu in the process.
    /// <para>
    /// Deliberately <see cref="ToolStripManager.Renderer"/> rather than the
    /// individual <c>ContextMenuStrip.Renderer</c>. A menu item's drop-down is a
    /// separate control that takes its renderer from the manager, not from the
    /// menu that owns the item - so setting it per-menu would theme the top
    /// level and leave the Windows submenu white. The submenu is also rebuilt
    /// constantly as windows come and go, and its items are created after this
    /// runs; going through the manager means they are themed on creation
    /// instead of needing to be walked and recoloured every rebuild.
    /// </para>
    /// </summary>
    public static void Apply()
    {
        ToolStripManager.Renderer = SystemTheme.IsDarkMode
            ? new DarkMenuRenderer()
            : new ToolStripProfessionalRenderer();
    }
}

/// <summary>
/// The dark palette, approximating what Windows 11 draws for its own menus.
/// </summary>
internal sealed class DarkMenuColorTable : ProfessionalColorTable
{
    internal static readonly Color Background = Color.FromArgb(43, 43, 43);
    internal static readonly Color Text = Color.FromArgb(255, 255, 255);
    internal static readonly Color DisabledText = Color.FromArgb(145, 145, 145);
    internal static readonly Color Highlight = Color.FromArgb(67, 67, 67);
    internal static readonly Color Edge = Color.FromArgb(82, 82, 82);

    public override Color ToolStripDropDownBackground => Background;
    public override Color MenuBorder => Edge;

    public override Color MenuItemSelected => Highlight;
    public override Color MenuItemSelectedGradientBegin => Highlight;
    public override Color MenuItemSelectedGradientEnd => Highlight;
    public override Color MenuItemBorder => Highlight;

    public override Color MenuItemPressedGradientBegin => Background;
    public override Color MenuItemPressedGradientMiddle => Background;
    public override Color MenuItemPressedGradientEnd => Background;

    // The strip down the left where check marks and icons go. Left the same as
    // the menu background rather than the lighter shade the light theme uses -
    // a paler gutter beside dark items reads as a rendering artefact.
    public override Color ImageMarginGradientBegin => Background;
    public override Color ImageMarginGradientMiddle => Background;
    public override Color ImageMarginGradientEnd => Background;

    public override Color SeparatorDark => Edge;
    public override Color SeparatorLight => Edge;

    public override Color CheckBackground => Highlight;
    public override Color CheckSelectedBackground => Highlight;
    public override Color CheckPressedBackground => Highlight;
}

/// <summary>
/// The dark renderer. The colour table covers everything the professional
/// renderer fills; text and the submenu chevron are drawn from the item's own
/// colours instead, so they have to be set here or they stay black on the dark
/// background.
/// </summary>
internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    public DarkMenuRenderer() : base(new DarkMenuColorTable())
    {
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        // Disabled items matter here: the Windows submenu shows a disabled
        // "(no windows found)" entry, and black-on-dark-grey would make the one
        // message explaining an empty list the least readable thing in it.
        e.TextColor = e.Item?.Enabled == false
            ? DarkMenuColorTable.DisabledText
            : DarkMenuColorTable.Text;

        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = DarkMenuColorTable.Text;
        base.OnRenderArrow(e);
    }
}
