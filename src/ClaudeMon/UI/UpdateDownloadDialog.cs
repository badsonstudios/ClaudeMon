namespace ClaudeMon.UI;

using System.Drawing;
using ClaudeMon.Services;

/// <summary>How <see cref="UpdateDownloadDialog"/> ended.</summary>
internal enum UpdateDownloadOutcome
{
    /// <summary>Cancelled (button, Esc, or close box) — nothing to do. The default.</summary>
    Cancelled,

    /// <summary>The download or verification failed and the user dismissed the error.</summary>
    Failed,

    /// <summary>The download failed and the user chose the release-page fallback.</summary>
    OpenReleasePage,

    /// <summary>
    /// The installer was launched and is (as far as we know) installing. The normal end state:
    /// the dialog usually never closes from this — the installer stops the process and the
    /// window dies with it. Seen by the caller only on the safety-timeout or user-dismiss
    /// exits, where the installer is still presumed running.
    /// </summary>
    Installing,

    /// <summary>
    /// The installer exited without installing (see TrayApplication.WatchInstallerProcess) —
    /// the in-progress claim must be released.
    /// </summary>
    InstallerAborted,
}

/// <summary>
/// Modal progress window for the in-app update: runs
/// <see cref="UpdateInstaller.DownloadAndVerifyAsync"/> with a progress bar and Cancel, and on
/// failure swaps to an error state offering the release page as the manual fallback. On
/// success it does NOT close (issue #94 — a sub-second download made the window a barely
/// visible flash, and the silent install that follows has no UI): it flips to an
/// "Installing…" marquee state and invokes the caller's launch callback, then stays up until
/// the installer stops the process — the window's disappearance IS the restart. Guards: a
/// safety timeout closes it if the installer never kills us, and
/// <see cref="InstallerExited"/> closes it when the installer aborts without installing.
/// Follows the UpdateAvailableDialog hand-scaled DPI/theme conventions.
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
    private readonly string _version;
    private readonly Func<string, bool> _launchInstaller;
    private readonly CancellationTokenSource _cts = new();
    private bool _finished;

    // If the installer hasn't stopped this process within this window, something is wrong
    // enough that holding a modal "Installing…" open forever helps nobody — close and let
    // WatchInstallerProcess's abort handling (or the user) take it from there.
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(2);
    private System.Windows.Forms.Timer? _installTimeout;

    public UpdateDownloadOutcome Outcome { get; private set; } = UpdateDownloadOutcome.Cancelled;

    /// <param name="version">The version being downloaded, already formatted (e.g. "0.13.0").</param>
    /// <param name="launchInstaller">
    /// Launches the verified installer at the given path, returning whether the process
    /// started (TrayApplication.LaunchDownloadedInstaller). Invoked on the UI thread from
    /// inside the modal pump when the download succeeds.
    /// </param>
    public UpdateDownloadDialog(
        UpdateInstaller installer, string installerUrl, string checksumUrl, string version,
        Func<string, bool> launchInstaller)
    {
        _installer = installer;
        _installerUrl = installerUrl;
        _checksumUrl = checksumUrl;
        _version = version;
        _launchInstaller = launchInstaller;

        Text = "ClaudeMon update";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        // Manual + CenterOnPrimary in OnLoad — all app dialogs open on the primary monitor (#88).
        StartPosition = FormStartPosition.Manual;
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
        DialogPlacement.CenterOnPrimary(this);
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
                BeginInstall(result.InstallerPath);
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
    /// Swaps to the installing state and hands the verified installer to the caller's
    /// launcher, keeping the window up for continuous feedback: the installer stops this
    /// process, so the window vanishing IS the restart. A launch failure falls into the
    /// existing error state (the launcher itself already opened the release-page fallback).
    /// Runs on the UI thread inside the modal pump.
    /// </summary>
    private void BeginInstall(string installerPath)
    {
        _heading.Text = $"Installing ClaudeMon v{_version}";
        _status.Text = "ClaudeMon will close and restart itself in a moment…";
        _progressBar.Style = ProgressBarStyle.Marquee;
        // Nothing to cancel any more: the installer is (about to be) a separate process.
        // Esc routes to this disabled button, so it goes inert too; the close box stays
        // available but is harmless — dismissing the window doesn't touch the installer.
        _cancelButton.Enabled = false;

        if (!_launchInstaller(installerPath))
        {
            ShowLaunchFailure();
            return;
        }

        Outcome = UpdateDownloadOutcome.Installing;
        // Belt-and-braces: if the installer never stops this process (hung, blocked by AV
        // before reaching the kill), don't hold a modal "Installing…" open forever.
        _installTimeout = new System.Windows.Forms.Timer { Interval = (int)InstallTimeout.TotalMilliseconds };
        _installTimeout.Tick += (_, _) => Close();
        _installTimeout.Start();
    }

    /// <summary>
    /// Called (on the UI thread) when the launched installer exited without installing —
    /// flips the outcome so the caller releases its in-progress claim, and closes.
    /// </summary>
    public void InstallerExited()
    {
        if (IsDisposed || Outcome != UpdateDownloadOutcome.Installing)
            return;
        _installTimeout?.Stop();
        Outcome = UpdateDownloadOutcome.InstallerAborted;
        Close();
    }

    /// <summary>
    /// The download and verify succeeded but the installer process couldn't be started.
    /// Distinct from <see cref="ShowError"/>: "couldn't be downloaded" would be false here,
    /// and the launcher's own failure path has already opened the release page — offering
    /// an "Open release page" button too would open it twice.
    /// </summary>
    private void ShowLaunchFailure()
    {
        Outcome = UpdateDownloadOutcome.Failed;
        _heading.Text = "The update couldn't be installed";
        _status.Text = "The installer couldn't be started. The release page has been opened "
            + "in your browser — you can install the update from there.";
        _progressBar.Visible = false;
        _cancelButton.Text = "Close";
        _cancelButton.Enabled = true;
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
        _cancelButton.Enabled = true; // BeginInstall disables it; the error state needs it back
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
            _installTimeout?.Dispose();
            _cts.Dispose();
            _baseFont.Dispose();
            _headingFont.Dispose();
        }
    }
}
