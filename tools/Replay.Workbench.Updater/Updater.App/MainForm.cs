using ReplayWorkbench.Updater;

namespace ReplayWorkbench.Updater.App;

/// <summary>
/// The patch update, start to finish: mirror the diffs, extend the chain,
/// regenerate the data files, re-export the desktop core's copy, rebuild the
/// editor, and validate the result against a recording.
///
/// <para>Preview always runs first. These files carry uncommitted work often
/// enough that git is not a reliable undo, so nothing is written until a preview
/// has been read and Apply is pressed — and Apply takes a timestamped backup
/// before it touches anything.</para>
/// </summary>
internal sealed class MainForm : Form
{
    private readonly string _repoRoot;

    private readonly TextBox _build = new();
    private readonly TextBox _recording = new();
    private readonly TextBox _stopAt = new();
    private readonly TextBox _diffs = new();
    private readonly CheckBox _verifyNames = new() { Text = "Cross-check names against FFXIVOpcodes", Checked = true };
    private readonly CheckBox _mergeNames = new() { Text = "Adopt published names the diff could not carry" };
    private readonly CheckBox _updateOld = new() { Text = "Also update docs/old/opcodes.js", Checked = true };
    private readonly CheckBox _exportCore = new() { Text = "Re-export the desktop core's embedded data", Checked = true };
    private readonly CheckBox _rebuild = new() { Text = "Rebuild the editor solution", Checked = true };
    private readonly CheckBox _runVerify = new() { Text = "Run post-update validation", Checked = true };
    private readonly ComboBox _configuration = new() { DropDownStyle = ComboBoxStyle.DropDownList };

    private readonly FlatButton _preview;
    private readonly FlatButton _apply;
    private readonly FlatButton _restore;
    private readonly TextBox _log = new();
    private readonly Label _status = new();

    private bool _previewed;
    private string? _lastBackup;
    private bool _busy;

    public MainForm(string repoRoot)
    {
        _repoRoot = repoRoot;
        Text = "Replay Workbench — patch update";
        BackColor = Theme.Bg;
        ForeColor = Theme.Ink;
        Font = Theme.Sans;
        // wide enough for a field plus its Browse button; narrower than this and
        // the anchored button would be pushed past the right edge
        ClientSize = new Size(1080, 900);
        MinimumSize = new Size(1100, 720);
        StartPosition = FormStartPosition.CenterScreen;

        _preview = new FlatButton("Preview") { Accent = true };
        _apply = new FlatButton("Apply") { Enabled = false };
        _restore = new FlatButton("Restore last backup") { Danger = true, Enabled = false };
        _preview.Click += (_, _) => Start(check: true);
        _apply.Click += (_, _) => Start(check: false);
        _restore.Click += (_, _) => RestoreBackup();

        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Both;
        _log.WordWrap = false;
        _log.BackColor = Theme.Panel;
        _log.ForeColor = Theme.InkDim;
        _log.Font = Theme.MonoSmall;
        _log.BorderStyle = BorderStyle.None;
        _log.Dock = DockStyle.Fill;

        var logHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Panel, Padding = new Padding(12), Margin = new Padding(0) };
        logHost.Controls.Add(_log);
        logHost.Paint += (_, e) => Theme.Frame(e.Graphics, logHost.ClientRectangle);

        var top = BuildForm();
        var actions = BuildActions();

        Controls.Add(logHost);
        Controls.Add(actions);
        Controls.Add(top);

        Say($"repo: {_repoRoot}");
        Say("Fill in what you have and press Preview. Nothing is written until you Apply.");
    }

    // ---- layout -----------------------------------------------------------

    private Control BuildForm()
    {
        var host = new Panel { Dock = DockStyle.Top, BackColor = Theme.Bg, Padding = new Padding(16, 14, 16, 6) };
        var y = 14;
        var labelW = TextRenderer.MeasureText("Local opcodediff diffs/", Theme.Sans).Width + 14;
        var rowH = Theme.Mono.Height + 16;

        TextBox Field(string caption, string hint, TextBox box, bool browseFile, bool browseDir)
        {
            host.Controls.Add(new Label
            {
                Text = caption, Font = Theme.Sans, ForeColor = Theme.Ink, UseMnemonic = false,
                AutoSize = false, Width = labelW, Height = rowH - 6,
                Location = new Point(16, y + 3), TextAlign = ContentAlignment.MiddleLeft,
            });
            // Build the browse button first: the field has to stop short of it, or
            // stretching the window pushes the button off the right edge.
            FlatButton? b = null;
            if (browseFile || browseDir) b = new FlatButton("Browse\u2026");
            var reserved = b is null ? 0 : b.Width + 10;

            box.Font = Theme.Mono;
            box.BackColor = Theme.Panel2;
            box.ForeColor = Theme.Ink;
            box.BorderStyle = BorderStyle.FixedSingle;
            box.SetBounds(16 + labelW, y, ClientSize.Width - (16 + labelW) - reserved - 16, rowH - 8);
            box.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            host.Controls.Add(box);

            if (b is not null)
            {
                b.Location = new Point(box.Right + 10, y - 2);
                b.Anchor = AnchorStyles.Top | AnchorStyles.Right;
                b.Click += (_, _) =>
                {
                    if (browseDir)
                    {
                        using var d = new FolderBrowserDialog { Description = caption };
                        if (d.ShowDialog(this) == DialogResult.OK) box.Text = d.SelectedPath;
                    }
                    else
                    {
                        using var d = new OpenFileDialog
                        {
                            Title = caption,
                            Filter = "FFXIV duty recording (*.dat)|*.dat|All files (*.*)|*.*",
                        };
                        if (d.ShowDialog(this) == DialogResult.OK) box.Text = d.FileName;
                    }
                };
                host.Controls.Add(b);
            }

            var h = new Label
            {
                Text = hint, Font = Theme.MonoSmall, ForeColor = Theme.InkFaint, UseMnemonic = false,
                AutoSize = false, Width = 700, Height = Theme.MonoSmall.Height + 2,
                Location = new Point(16 + labelW, y + rowH - 7),
            };
            host.Controls.Add(h);
            y += rowH + Theme.MonoSmall.Height + 4;
            return box;
        }

        Field("Game build number", "int32 at 0x10 of a .dat. Leave empty to update the chain only.", _build, false, false);
        Field("…or a recording", "reads the build out of a recording made on the new patch.", _recording, true, false);
        Field("Stop at patch", "optional. Defaults to the newest diff published.", _stopAt, false, false);
        Field("Local opcodediff diffs/", "optional. Saves downloading; a sibling checkout is found automatically.", _diffs, false, true);

        foreach (var c in new[] { _verifyNames, _mergeNames, _updateOld, _exportCore, _rebuild, _runVerify })
        {
            c.Font = Theme.Sans;
            c.ForeColor = Theme.Ink;
            c.BackColor = Theme.Bg;
            c.FlatStyle = FlatStyle.Flat;
            c.FlatAppearance.BorderColor = Theme.Line;
            c.AutoSize = true;
            c.UseMnemonic = false;
        }
        var left = 16 + labelW;
        _verifyNames.Location = new Point(left, y);
        _mergeNames.Location = new Point(left + 380, y);
        y += _verifyNames.Height + 6;
        _updateOld.Location = new Point(left, y);
        _exportCore.Location = new Point(left + 380, y);
        y += _updateOld.Height + 6;
        _rebuild.Location = new Point(left, y);
        _runVerify.Location = new Point(left + 380, y);
        y += _rebuild.Height + 10;

        host.Controls.AddRange(new Control[] { _verifyNames, _mergeNames, _updateOld, _exportCore, _rebuild, _runVerify });

        host.Controls.Add(new Label
        {
            Text = "Build configuration", Font = Theme.Sans, ForeColor = Theme.Ink, UseMnemonic = false,
            AutoSize = false, Width = labelW, Height = rowH - 6, Location = new Point(16, y + 2),
            TextAlign = ContentAlignment.MiddleLeft,
        });
        _configuration.Items.AddRange(new object[] { "Debug", "Release" });
        _configuration.SelectedIndex = 0;
        _configuration.Font = Theme.Mono;
        _configuration.BackColor = Theme.Panel2;
        _configuration.ForeColor = Theme.Ink;
        _configuration.FlatStyle = FlatStyle.Flat;
        _configuration.SetBounds(left, y, 140, rowH - 8);
        host.Controls.Add(_configuration);
        y += rowH + 6;

        host.Height = y;
        return host;
    }

    private Control BuildActions()
    {
        var host = new Panel { Dock = DockStyle.Top, BackColor = Theme.Bg, Padding = new Padding(16, 0, 16, 10), Height = 54 };
        var flow = new FlowLayoutPanel { Dock = DockStyle.Left, AutoSize = true, WrapContents = false, BackColor = Theme.Bg };
        flow.Controls.AddRange(new Control[] { _preview, _apply, _restore });

        _status.Font = Theme.MonoSmall;
        _status.ForeColor = Theme.InkDim;
        _status.AutoSize = false;
        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Padding = new Padding(14, 0, 0, 0);

        host.Controls.Add(_status);
        host.Controls.Add(flow);
        return host;
    }

    // ---- running ----------------------------------------------------------

    private UpdateOptions ReadOptions(bool check)
    {
        var o = new UpdateOptions
        {
            Check = check,
            To = Blank(_stopAt.Text),
            DiffsDir = Blank(_diffs.Text),
            FromReplay = Blank(_recording.Text),
            NoOld = !_updateOld.Checked,
            VerifyNames = _verifyNames.Checked,
            MergeNewNames = _mergeNames.Checked,
            ExportCoreData = _exportCore.Checked,
        };
        var build = Blank(_build.Text);
        if (build is not null)
        {
            if (!int.TryParse(build, out var n) || n <= 0)
                throw new FatalException("Game build number must be a positive integer.");
            o.Build = n;
        }
        if (o.FromReplay is not null && !File.Exists(o.FromReplay))
            throw new FatalException($"recording not found: {o.FromReplay}");
        return o;

        static string? Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    private void Start(bool check)
    {
        if (_busy) return;
        UpdateOptions options;
        try { options = ReadOptions(check); }
        catch (FatalException e) { Say(e.Message, error: true); return; }

        if (!check && !_previewed)
        {
            Say("Run Preview first — Apply writes to the data files.", error: true);
            return;
        }
        if (!check && _rebuild.Checked && Pipeline.RunningEditors().Count > 0)
        {
            Say("Close Replay.Workbench before applying: a rebuild cannot overwrite its running binaries.", error: true);
            return;
        }

        _busy = true;
        SetEnabled(false);
        _log.Clear();
        _status.Text = check ? "previewing…" : "applying…";
        _status.ForeColor = Theme.Amber;

        Task.Run(() =>
        {
            var ok = true;
            try
            {
                if (!check) _lastBackup = TrackedFiles.Backup(_repoRoot, Log);

                var result = UpdateRunner.Run(_repoRoot, options, Log);
                ok = result.Ok;

                if (ok && !check && _rebuild.Checked)
                {
                    Say("\n== rebuild ==");
                    ok = Pipeline.BuildEditor(_repoRoot, Configuration, Log);
                }
                if (ok && !check && _runVerify.Checked && _rebuild.Checked)
                {
                    Say("\n== validate ==");
                    ok = Pipeline.Verify(_repoRoot, Configuration, options.FromReplay, Log);
                    if (!ok) Say("validation failed — the data written may be wrong; consider restoring the backup.");
                }
            }
            catch (Exception e)
            {
                ok = false;
                Say("error: " + e.Message, error: true);
            }

            BeginInvoke(() =>
            {
                _busy = false;
                SetEnabled(true);
                if (check && ok) _previewed = true;
                if (!check) _restore.Enabled = _lastBackup is not null;
                _status.Text = ok
                    ? check ? "preview complete — review the log, then Apply" : "update complete"
                    : "failed — see the log";
                _status.ForeColor = ok ? Theme.Phosphor : Theme.Danger;
            });
        });
    }

    private string Configuration => (string)_configuration.SelectedItem!;

    private void RestoreBackup()
    {
        if (_lastBackup is null) return;
        var answer = MessageBox.Show(this,
            $"Put back the files saved before the last apply?\n\n{_lastBackup}",
            "Restore backup", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (answer != DialogResult.Yes) return;
        try
        {
            TrackedFiles.Restore(_repoRoot, _lastBackup, Log);
            _status.Text = "backup restored";
            _status.ForeColor = Theme.Amber;
        }
        catch (Exception e) { Say("error: " + e.Message, error: true); }
    }

    private void SetEnabled(bool on)
    {
        _preview.Enabled = on;
        _apply.Enabled = on && _previewed;
        _restore.Enabled = on && _lastBackup is not null;
    }

    /// <summary>Plain log sink, for the Action&lt;string&gt; the pipeline steps take.</summary>
    private void Log(string message) => Say(message);

    private void Say(string message, bool error = false)
    {
        if (InvokeRequired) { BeginInvoke(() => Say(message, error)); return; }
        _log.AppendText((error ? "! " : "") + message + Environment.NewLine);
        if (!error) return;
        _status.Text = message;
        _status.ForeColor = Theme.Danger;
    }
}
