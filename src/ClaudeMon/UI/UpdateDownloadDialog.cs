namespace ClaudeMon.UI;

using System.Drawing;
using ClaudeMon.Services;

/// <summary>How <see cref="UpdateDownloadDialog"/> ended.</summary>
internal enum UpdateDownloadOutcome
{
    /// <summary>Cancelled (button, Esc, or close box) — nothing to do. The default.</summary>
    Cancelled,

    /// <summary>The installer downloaded and verified; <see cref="UpdateDownloadDialog.InstallerPath"/> is set.</summary>
    Downloaded,

    /// <summary>The download or verification failed and the user dismissed the error.</summary>
    Failed,

    /// <summary>The download failed and the user chose the release-page fallback.</summary>
    OpenReleasePage,
}

/// <summary>
/// Modal progress window for the in-app update download: runs
/// <see cref="UpdateInstaller.DownloadAndVerifyAsync"/> with a progress bar and Cancel, and on
/// failure swaps to an error state offering the release page as the manual fallback. Like
/// <see cref="UpdateAvailableDialog"/> it only collects an outcome — the caller launches the
/// installer — and follows the same hand-scaled DPI/theme conventions.
/// </summary>
internal sealed class UpdateDownloadDialog : Form
{
    // --- Layout metrics, logical (96-DPI) units ---
    private const int Pad = 24;
    private const int ClientWidth = 430;
    private const int ContentRight = ClientWidth - Pad;
    private const int HeadingTop = 20;
    private const int StatusTop = 52;
    private const int StatusHeight = 34; // two lines, for wrapped error messages
    private const int ProgressTop = 92;
    private const int ProgressHeight = 14;
    private const int ButtonsTop = 122;
    private const int ButtonHeight = 30;
    private const int ButtonGap = 8;
    private const int CancelButtonWidth = 82;
    private const int OpenPageButtonWidth = 136;

    private readonly Theme _theme = Theme.Current;

    // Owned by this form: WinForms never disposes a Font you assign to a control (see
    // SettingsForm._baseFont), so these must be disposed with the dialog.
    private readonly Font _baseFont = new("Segoe UI", 9.75f);
    private readonly Font _headingFont = new("Segoe UI Semibold", 11.25f);

    private readonly Label _heading;
    private readonly Label _status;
    private readonly ProgressBar _progressBar;
    private readonly Button _cancelButton;
    private readonly Button _openPageButton;

    private readonly UpdateInstaller _installer;
    private readonly string _installerUrl;
    private readonly string _checksumUrl;
    private readonly CancellationTokenSource _cts = new();
    private bool _finished;

    public UpdateDownloadOutcome Outcome { get; private set; } = UpdateDownloadOutcome.Cancelled;

    /// <summary>Local path of the verified installer once <see cref="Outcome"/> is Downloaded.</summary>
    public string? InstallerPath { get; private set; }

    /// <param name="version">The version being downloaded, already formatted (e.g. "0.13.0").</param>
    public UpdateDownloadDialog(
        UpdateInstaller installer, string installerUrl, string checksumUrl, string version)
    {
        _installer = installer;
        _installerUrl = installerUrl;
        _checksumUrl = checksumUrl;

        Text = "ClaudeMon update";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        // Same reasoning as UpdateAvailableDialog: this can open from a background check with no
        // owner window, where the foreground lock would bury it. Cancel is one Esc away.
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None;
        Font = _baseFont;

        _heading = new Label
        {
            Text = $"Downloading ClaudeMon v{version}",
            AutoSize = true,
            Font = _headingFont,
            ForeColor = _theme.HeaderAccent,
        };
        Controls.Add(_heading);

        _status = new Label
        {
            Text = "Starting download…",
            AutoSize = false, // fixed two-line box so wrapped error text fits
            ForeColor = _theme.HintText,
        };
        Controls.Add(_status);

        _progressBar = new ProgressBar { Minimum = 0, Maximum = 100 };
        Controls.Add(_progressBar);

        _cancelButton = MakeButton("Cancel");
        _cancelButton.Click += (_, _) => Close(); // OnFormClosing turns this into a cancellation

        _openPageButton = MakeButton("Open release page");
        _openPageButton.Visible = false;
        _openPageButton.Click += (_, _) =>
        {
            Outcome = UpdateDownloadOutcome.OpenReleasePage;
            Close();
        };

        CancelButton = _cancelButton;

        Relayout();
    }

    private Button MakeButton(string text)
    {
        var button = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = _theme.ButtonBack,
            ForeColor = _theme.ButtonText,
        };
        button.FlatAppearance.BorderColor = _theme.ButtonBorder;
        Controls.Add(button);
        return button;
    }

    private int Sc(int value) => DpiScale.Scale(value, DeviceDpi / 96f);

    private void Relayout()
    {
        _heading.Location = new Point(Sc(Pad), Sc(HeadingTop));
        _status.SetBounds(Sc(Pad), Sc(StatusTop), Sc(ContentRight - Pad), Sc(StatusHeight));
        _progressBar.SetBounds(Sc(Pad), Sc(ProgressTop), Sc(ContentRight - Pad), Sc(ProgressHeight));

        var buttonsTop = Sc(ButtonsTop);
        _cancelButton.SetBounds(
            Sc(ContentRight - CancelButtonWidth), buttonsTop, Sc(CancelButtonWidth), Sc(ButtonHeight));
        _openPageButton.SetBounds(
            Sc(ContentRight - CancelButtonWidth - ButtonGap - OpenPageButtonWidth), buttonsTop,
            Sc(OpenPageButtonWidth), Sc(ButtonHeight));

        ClientSize = new Size(Sc(ClientWidth), buttonsTop + Sc(ButtonHeight) + Sc(Pad));
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // DeviceDpi is only reliable once the handle exists (see UpdateAvailableDialog.OnLoad).
        Relayout();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        Relayout();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SystemTheme.ApplyTitleBar(Handle, _theme.IsDark);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        _ = RunDownloadAsync();
    }

    private async Task RunDownloadAsync()
    {
        // Progress<T> is created on the UI thread, so reports marshal back here on their own.
        // The guards cover reports queued behind a cancel ("Cancelling…" must not be overwritten)
        // or behind a non-user close (the form may already be disposed — see OnFormClosing).
        var progress = new Progress<double>(fraction =>
        {
            if (IsDisposed || _cts.IsCancellationRequested)
                return;
            var percent = Math.Clamp((int)(fraction * 100), 0, 100);
            _progressBar.Value = percent;
            _status.Text = $"Downloading… {percent}%";
        });

        try
        {
            var result = await _installer.DownloadAndVerifyAsync(
                _installerUrl, _checksumUrl, progress, _cts.Token);
            _finished = true;

            // A non-user close (app exit, Windows shutdown) may have disposed the form while the
            // download was still in flight; leave Outcome as Cancelled and don't touch controls.
            if (IsDisposed)
                return;

            if (result.InstallerPath is not null)
            {
                InstallerPath = result.InstallerPath;
                Outcome = UpdateDownloadOutcome.Downloaded;
                Close();
            }
            else
            {
                ShowError();
            }
        }
        catch (OperationCanceledException)
        {
            // The user cancelled (Outcome stays Cancelled — OnFormClosing held the close until
            // this point) or a non-user close disposed the form, which needs no re-close.
            _finished = true;
            if (!IsDisposed)
                Close();
        }
    }

    /// <summary>
    /// Swaps the dialog to its failure state: the progress bar goes away, the status explains,
    /// and the buttons become "Open release page" (the manual fallback) / "Close". The error
    /// detail is deliberately generic — the user can't act on an HTTP status, and the release
    /// page works no matter what failed.
    /// </summary>
    private void ShowError()
    {
        Outcome = UpdateDownloadOutcome.Failed;
        _heading.Text = "The update couldn't be downloaded";
        _status.Text = "You can download it yourself from the GitHub release page instead.";
        _progressBar.Visible = false;
        _openPageButton.Visible = true;
        _cancelButton.Text = "Close";
        AcceptButton = _openPageButton;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_finished)
        {
            // A user close (Cancel, Esc, the close box) must not leave the task writing to a
            // dead form: hold the close, signal cancellation, and let RunDownloadAsync's
            // cancellation path re-close once the task has actually stopped. A NON-user close
            // (tray Exit → Application.Exit, Windows shutdown) must NOT be held — cancelling it
            // aborts the whole app exit, silently eating the user's Exit click — so it proceeds
            // and the task's IsDisposed guards absorb the stragglers.
            _cts.Cancel();
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                _status.Text = "Cancelling…";
                _cancelButton.Enabled = false;
            }
        }

        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _cts.Dispose();
            _baseFont.Dispose();
            _headingFont.Dispose();
        }
    }
}
