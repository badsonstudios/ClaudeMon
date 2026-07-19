namespace ClaudeMon.UI;

using System.Drawing;
using ClaudeMon.Services;

/// <summary>The user's choice in <see cref="UpdateAvailableDialog"/>.</summary>
internal enum UpdateDialogChoice
{
    /// <summary>Dismiss for now; later checks remind again. Also the Esc / close-box result.</summary>
    Ignore,

    /// <summary>Open the release page.</summary>
    GetUpdate,

    /// <summary>Skip this specific version: no more automatic prompts for it.</summary>
    SkipVersion,
}

/// <summary>
/// The compact "a new version is available" window that replaced the update tray balloon:
/// Get the update / Ignore / Skip this version. Follows the SettingsForm conventions —
/// <c>AutoScaleMode.None</c> with every metric hand-scaled from logical (96-DPI) units via
/// <see cref="DpiScale"/>, the <see cref="Theme"/> accents over the app-wide colour mode, and a
/// re-layout on load and on DPI change. The dialog only collects the answer (read
/// <see cref="Choice"/> after <c>ShowDialog</c>); the caller opens the release page or persists
/// the skipped version.
/// </summary>
internal sealed class UpdateAvailableDialog : Form
{
    // --- Layout metrics, logical (96-DPI) units ---
    private const int Pad = 24;
    private const int ClientWidth = 430;
    private const int ContentRight = ClientWidth - Pad;
    private const int HeadingTop = 20;
    private const int VersionsTop = 52;
    private const int NotesTop = 76;
    private const int ButtonsTop = 118;
    private const int ButtonHeight = 30;
    private const int ButtonGap = 8;
    private const int GetButtonWidth = 118;
    private const int IgnoreButtonWidth = 82;
    private const int SkipButtonWidth = 126;

    private readonly Theme _theme = Theme.Current;

    // Owned by this form: WinForms never disposes a Font you assign to a control (see
    // SettingsForm._baseFont), so these must be disposed with the dialog.
    private readonly Font _baseFont = new("Segoe UI", 9.75f);
    private readonly Font _headingFont = new("Segoe UI Semibold", 11.25f);

    private readonly Label _heading;
    private readonly Label _versions;
    private readonly LinkLabel _releaseNotesLink;
    private readonly Button _getButton;
    private readonly Button _ignoreButton;
    private readonly Button _skipButton;

    public UpdateDialogChoice Choice { get; private set; } = UpdateDialogChoice.Ignore;

    /// <param name="currentVersion">The running version, already formatted (e.g. "0.11.0").</param>
    /// <param name="latestVersion">The newer release's version, already formatted.</param>
    /// <param name="releaseNotesUrl">
    /// The offered release's GitHub page (callers pass their already-fallbacked URL). The
    /// "View release notes" link opens it without closing the dialog; a null/non-http(s)
    /// value hides the link rather than leaving a dead control.
    /// </param>
    public UpdateAvailableDialog(string currentVersion, string latestVersion, string? releaseNotesUrl)
    {
        Text = "ClaudeMon update";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        // Manual + CenterOnPrimary in OnLoad: CenterScreen would follow the mouse cursor's
        // monitor, which for this timer-popped dialog means a random side monitor (#88).
        StartPosition = FormStartPosition.Manual;
        // The automatic check opens this with no owner window from a background timer, where
        // the foreground lock would otherwise leave it buried behind (and unfocusable under)
        // whatever the user is working in — the very failure mode of the balloon it replaced.
        // Ignore is one Esc away, so staying on top is the lesser evil.
        TopMost = true;
        // Manual layout, like the other hand-scaled windows: WinForms auto-scaling would fight
        // the Sc()-based Relayout, so we own all scaling (point-sized fonts scale on their own).
        AutoScaleMode = AutoScaleMode.None;
        Font = _baseFont;

        _heading = new Label
        {
            Text = "A new version of ClaudeMon is available",
            AutoSize = true,
            Font = _headingFont,
            ForeColor = _theme.HeaderAccent,
        };
        Controls.Add(_heading);

        _versions = new Label
        {
            Text = $"You have v{currentVersion} — v{latestVersion} is available.",
            AutoSize = true,
            ForeColor = _theme.HintText,
        };
        Controls.Add(_versions);

        _releaseNotesLink = new LinkLabel
        {
            Text = "View release notes",
            AutoSize = true,
            LinkBehavior = LinkBehavior.HoverUnderline,
            LinkColor = _theme.HeaderAccent,
            ActiveLinkColor = _theme.HeaderAccent,
            Visible = BrowserLauncher.IsSafeHttpUrl(releaseNotesUrl, out _),
        };
        // Opens the browser but deliberately leaves the dialog up (no DialogResult), so the
        // user can read what changed and then still choose Get / Ignore / Skip.
        _releaseNotesLink.LinkClicked += (_, _) => BrowserLauncher.TryOpenHttp(releaseNotesUrl);
        Controls.Add(_releaseNotesLink);

        _getButton = MakeButton("Get the update", UpdateDialogChoice.GetUpdate);
        _ignoreButton = MakeButton("Ignore", UpdateDialogChoice.Ignore);
        _skipButton = MakeButton("Skip this version", UpdateDialogChoice.SkipVersion);

        AcceptButton = _getButton;
        // Esc / the close box mean "not now", never "skip": Choice already defaults to Ignore.
        CancelButton = _ignoreButton;

        // The link was added to Controls first, which would make it the initial focus; keep
        // the tab order (and therefore first focus) on the action buttons, as before the link
        // existed. The link stays mouse-only in practice: with an AcceptButton set, Enter is
        // claimed by the form before a focused LinkLabel ever sees it (inherent WinForms
        // AcceptButton + LinkLabel interaction).
        _getButton.TabIndex = 0;
        _ignoreButton.TabIndex = 1;
        _skipButton.TabIndex = 2;
        _releaseNotesLink.TabIndex = 3;

        Relayout();
    }

    private Button MakeButton(string text, UpdateDialogChoice choice)
    {
        var button = new Button
        {
            Text = text,
            DialogResult = DialogResult.OK, // any non-None result closes the dialog
            FlatStyle = FlatStyle.Flat,
            BackColor = _theme.ButtonBack,
            ForeColor = _theme.ButtonText,
        };
        button.FlatAppearance.BorderColor = _theme.ButtonBorder;
        button.Click += (_, _) => Choice = choice;
        Controls.Add(button);
        return button;
    }

    private int Sc(int value) => DpiScale.Scale(value, DeviceDpi / 96f);

    // Positions everything from the logical metrics at the current monitor DPI. "Skip this
    // version" sits alone on the left, away from the Ignore/Get pair, so the destructive-ish
    // choice isn't next to the default one.
    private void Relayout()
    {
        _heading.Location = new Point(Sc(Pad), Sc(HeadingTop));
        _versions.Location = new Point(Sc(Pad), Sc(VersionsTop));
        _releaseNotesLink.Location = new Point(Sc(Pad), Sc(NotesTop));

        var buttonsTop = Sc(ButtonsTop);
        _skipButton.SetBounds(Sc(Pad), buttonsTop, Sc(SkipButtonWidth), Sc(ButtonHeight));
        _getButton.SetBounds(
            Sc(ContentRight - GetButtonWidth), buttonsTop, Sc(GetButtonWidth), Sc(ButtonHeight));
        _ignoreButton.SetBounds(
            Sc(ContentRight - GetButtonWidth - ButtonGap - IgnoreButtonWidth), buttonsTop,
            Sc(IgnoreButtonWidth), Sc(ButtonHeight));

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
        // Ask for keyboard focus so Enter/Esc work immediately. If another app holds the
        // foreground lock Windows may only flash the taskbar button — TopMost still keeps the
        // dialog visible either way.
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
