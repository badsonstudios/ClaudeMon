namespace ClaudeMon.UI;

using System.Drawing;
using ClaudeMon.Services;

/// <summary>
/// The About window, replacing the old <c>MessageBox</c> so the repository link can be a
/// real, clickable link (a MessageBox can only show inert text — issue #86). Same content
/// as the MessageBox it replaces: version, description, current monitor status, repo link.
/// Follows the <see cref="UpdateAvailableDialog"/> conventions — <c>AutoScaleMode.None</c>
/// with every metric hand-scaled from logical (96-DPI) units via <see cref="DpiScale"/>,
/// the <see cref="Theme"/> accents, and a re-layout on load and on DPI change. Unlike the
/// update dialog it is not TopMost: About is always user-initiated from the tray menu, so
/// it takes the foreground normally.
/// </summary>
internal sealed class AboutDialog : Form
{
    /// <summary>Where the link goes — also shown as the link's text (minus the scheme).</summary>
    private const string RepoUrl = "https://github.com/badsonstudios/ClaudeMon";

    // --- Layout metrics, logical (96-DPI) units. Unlike UpdateAvailableDialog's all-fixed
    // tops, only the first two rows are fixed: the description wraps to a DPI-dependent
    // height, so everything below it flows from the previous control's bottom in Relayout()
    // (fixed tops under a wrapping label collide the moment the text changes). ---
    private const int Pad = 24;
    private const int ClientWidth = 380;
    private const int ContentRight = ClientWidth - Pad;
    private const int HeadingTop = 20;
    private const int DescriptionTop = 52;
    private const int SectionGap = 12;
    private const int LinkGap = 8;
    private const int ButtonsGap = 16;
    private const int ButtonHeight = 30;
    private const int OkButtonWidth = 82;

    private readonly Theme _theme = Theme.Current;

    // Owned by this form: WinForms never disposes a Font you assign to a control (see
    // SettingsForm._baseFont), so these must be disposed with the dialog.
    private readonly Font _baseFont = new("Segoe UI", 9.75f);
    private readonly Font _headingFont = new("Segoe UI Semibold", 11.25f);

    private readonly Label _heading;
    private readonly Label _description;
    private readonly Label _status;
    private readonly LinkLabel _repoLink;
    private readonly Button _okButton;

    /// <param name="version">The running version, already formatted (e.g. "0.17.0").</param>
    /// <param name="statusText">The monitor status line, already humanized.</param>
    public AboutDialog(string version, string statusText)
    {
        Text = "About ClaudeMon";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        // Manual + CenterOnPrimary in OnLoad — all app dialogs open on the primary monitor (#88).
        StartPosition = FormStartPosition.Manual;
        // Manual layout, like the other hand-scaled windows: WinForms auto-scaling would fight
        // the Sc()-based Relayout, so we own all scaling (point-sized fonts scale on their own).
        AutoScaleMode = AutoScaleMode.None;
        Font = _baseFont;

        _heading = new Label
        {
            Text = $"ClaudeMon v{version}",
            AutoSize = true,
            Font = _headingFont,
            ForeColor = _theme.HeaderAccent,
        };
        Controls.Add(_heading);

        _description = new Label
        {
            Text = "Windows system tray monitor for Claude AI usage.\n\n" +
                   "Monitors 5-hour and 7-day rate limits for Claude Max subscribers.",
            AutoSize = true,
            MaximumSize = new Size(0, 0), // sized in Relayout for the current DPI
            ForeColor = _theme.HintText,
        };
        Controls.Add(_description);

        _status = new Label
        {
            Text = $"Status: {statusText}",
            AutoSize = true,
            ForeColor = _theme.HintText,
        };
        Controls.Add(_status);

        _repoLink = new LinkLabel
        {
            // Derived from the constant so the display can never drift from the destination.
            Text = RepoUrl["https://".Length..],
            AutoSize = true,
            LinkBehavior = LinkBehavior.HoverUnderline,
            LinkColor = _theme.HeaderAccent,
            ActiveLinkColor = _theme.HeaderAccent,
        };
        // Opens the browser and leaves the dialog up. The URL is a compile-time https
        // constant, but it still goes through the shared http(s)-only guard on principle.
        _repoLink.LinkClicked += (_, _) => BrowserLauncher.TryOpenHttp(RepoUrl);
        Controls.Add(_repoLink);

        _okButton = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            FlatStyle = FlatStyle.Flat,
            BackColor = _theme.ButtonBack,
            ForeColor = _theme.ButtonText,
        };
        _okButton.FlatAppearance.BorderColor = _theme.ButtonBorder;
        Controls.Add(_okButton);

        // OK is both Enter and Esc, so the keyboard always closes the dialog and Enter can
        // never collide with a focused link (the AcceptButton claims it first regardless).
        AcceptButton = _okButton;
        CancelButton = _okButton;

        // Keep initial focus on OK, not the link (Controls order would otherwise pick the link).
        _okButton.TabIndex = 0;
        _repoLink.TabIndex = 1;

        Relayout();
    }

    private int Sc(int value) => DpiScale.Scale(value, DeviceDpi / 96f);

    // Positions everything at the current monitor DPI. The wrap width must be set before
    // reading _description.Bottom — AutoSize recomputes the height from it.
    private void Relayout()
    {
        _heading.Location = new Point(Sc(Pad), Sc(HeadingTop));
        _description.MaximumSize = new Size(Sc(ContentRight - Pad), 0);
        _description.Location = new Point(Sc(Pad), Sc(DescriptionTop));

        _status.Location = new Point(Sc(Pad), _description.Bottom + Sc(SectionGap));
        _repoLink.Location = new Point(Sc(Pad), _status.Bottom + Sc(LinkGap));

        var buttonsTop = _repoLink.Bottom + Sc(ButtonsGap);
        _okButton.SetBounds(
            Sc(ContentRight - OkButtonWidth), buttonsTop, Sc(OkButtonWidth), Sc(ButtonHeight));

        ClientSize = new Size(Sc(ClientWidth), buttonsTop + Sc(ButtonHeight) + Sc(Pad));
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // DeviceDpi is only reliable once the handle exists; the constructor's Relayout ran at
        // the default DPI, so redo it here (before first paint) at the real monitor DPI.
        Relayout();
        DialogPlacement.CenterOnPrimary(this);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        // Re-fit if the dialog is dragged to a monitor with a different scale.
        Relayout();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // Ask for keyboard focus so Enter/Esc work immediately.
        Activate();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Match the title bar to the body (the app-wide colour mode usually handles this; this
        // makes it certain on Win10 20H1+/Win11).
        SystemTheme.ApplyTitleBar(Handle, _theme.IsDark);
    }

    protected override void Dispose(bool disposing)
    {
        // Dispose child controls first, then the fonts they were using (a control never disposes
        // an assigned Font itself).
        base.Dispose(disposing);
        if (disposing)
        {
            _baseFont.Dispose();
            _headingFont.Dispose();
        }
    }
}
