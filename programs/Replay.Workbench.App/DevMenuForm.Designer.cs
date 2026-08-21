#nullable disable

namespace ReplayWorkbench.App;

partial class DevMenuForm
{
    private System.ComponentModel.IContainer components = null;

    protected override void Dispose(bool disposing)
    {
        if (disposing && components != null) components.Dispose();
        base.Dispose(disposing);
    }

    #region Windows Form Designer generated code

    private void InitializeComponent()
    {
        this.pad = new ReplayWorkbench.App.RulePanel();
        this.buildLabel = new System.Windows.Forms.Label();
        this.buildBox = new ReplayWorkbench.App.DarkTextBox();
        this.jsonLabel = new System.Windows.Forms.Label();
        this.jsonBox = new ReplayWorkbench.App.DarkTextBox();
        this.hintLabel = new ReplayWorkbench.App.HintLabel();
        this.prefillButton = new ReplayWorkbench.App.FlatButton();
        this.cancelButton = new ReplayWorkbench.App.FlatButton();
        this.applyButton = new ReplayWorkbench.App.FlatButton();
        this.pad.SuspendLayout();
        this.SuspendLayout();
        //
        // pad
        //
        this.pad.Controls.Add(this.applyButton);
        this.pad.Controls.Add(this.cancelButton);
        this.pad.Controls.Add(this.prefillButton);
        this.pad.Controls.Add(this.hintLabel);
        this.pad.Controls.Add(this.jsonBox);
        this.pad.Controls.Add(this.jsonLabel);
        this.pad.Controls.Add(this.buildBox);
        this.pad.Controls.Add(this.buildLabel);
        this.pad.Dock = System.Windows.Forms.DockStyle.Fill;
        this.pad.Edge = ReplayWorkbench.App.RuleEdge.None;
        this.pad.Location = new System.Drawing.Point(0, 0);
        this.pad.Name = "pad";
        this.pad.Padding = new System.Windows.Forms.Padding(16);
        this.pad.Size = new System.Drawing.Size(620, 470);
        this.pad.TabIndex = 0;
        //
        // buildLabel
        //
        this.buildLabel.AutoSize = true;
        this.buildLabel.Font = ReplayWorkbench.App.Theme.SansBold;
        this.buildLabel.ForeColor = ReplayWorkbench.App.Theme.Ink;
        this.buildLabel.Location = new System.Drawing.Point(16, 16);
        this.buildLabel.Name = "buildLabel";
        this.buildLabel.Size = new System.Drawing.Size(130, 15);
        this.buildLabel.TabIndex = 0;
        this.buildLabel.Text = "Game build number";
        //
        // buildBox
        //
        this.buildBox.Location = new System.Drawing.Point(16, 38);
        this.buildBox.Name = "buildBox";
        this.buildBox.Size = new System.Drawing.Size(200, 24);
        this.buildBox.TabIndex = 1;
        //
        // jsonLabel
        //
        this.jsonLabel.AutoSize = true;
        this.jsonLabel.Font = ReplayWorkbench.App.Theme.SansBold;
        this.jsonLabel.ForeColor = ReplayWorkbench.App.Theme.Ink;
        this.jsonLabel.Location = new System.Drawing.Point(16, 72);
        this.jsonLabel.Name = "jsonLabel";
        this.jsonLabel.Size = new System.Drawing.Size(90, 15);
        this.jsonLabel.TabIndex = 2;
        this.jsonLabel.Text = "Opcodes JSON";
        //
        // jsonBox
        //
        this.jsonBox.Location = new System.Drawing.Point(16, 94);
        this.jsonBox.Multiline = true;
        this.jsonBox.Name = "jsonBox";
        this.jsonBox.PlaceholderText = "{ \"ActorCast\":457, \"ActorControl\":415, \"NpcSpawn\":888, … }";
        this.jsonBox.ScrollBars = System.Windows.Forms.ScrollBars.Vertical;
        this.jsonBox.Size = new System.Drawing.Size(588, 240);
        this.jsonBox.TabIndex = 3;
        this.jsonBox.WordWrap = false;
        //
        // hintLabel
        //
        this.hintLabel.BackColor = ReplayWorkbench.App.Theme.Panel;
        this.hintLabel.Location = new System.Drawing.Point(16, 346);
        this.hintLabel.Name = "hintLabel";
        this.hintLabel.Size = new System.Drawing.Size(588, 66);
        this.hintLabel.TabIndex = 4;
        this.hintLabel.Text = "Registers this opcode table for the build, then re-parses the loaded file. Plain {" +
            "name:opcode} maps and a full FFXIVOpcodes opcodes.json are both accepted.";
        //
        // prefillButton
        //
        this.prefillButton.AutoFit = false;
        this.prefillButton.Location = new System.Drawing.Point(16, 418);
        this.prefillButton.Name = "prefillButton";
        this.prefillButton.Size = new System.Drawing.Size(130, 31);
        this.prefillButton.TabIndex = 5;
        this.prefillButton.Text = "Prefill latest";
        this.prefillButton.Click += new System.EventHandler(this.OnPrefillClick);
        //
        // cancelButton
        //
        this.cancelButton.AutoFit = false;
        this.cancelButton.Location = new System.Drawing.Point(394, 418);
        this.cancelButton.Name = "cancelButton";
        this.cancelButton.Size = new System.Drawing.Size(100, 31);
        this.cancelButton.TabIndex = 6;
        this.cancelButton.Text = "Cancel";
        this.cancelButton.Click += new System.EventHandler(this.OnCancelClick);
        //
        // applyButton
        //
        this.applyButton.Accent = true;
        this.applyButton.AutoFit = false;
        this.applyButton.Location = new System.Drawing.Point(504, 418);
        this.applyButton.Name = "applyButton";
        this.applyButton.Size = new System.Drawing.Size(100, 31);
        this.applyButton.TabIndex = 7;
        this.applyButton.Text = "Apply";
        this.applyButton.Click += new System.EventHandler(this.OnApplyClick);
        //
        // DevMenuForm
        //
        this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.None;
        this.BackColor = ReplayWorkbench.App.Theme.Panel;
        this.CancelButton = this.cancelButton;
        this.ClientSize = new System.Drawing.Size(620, 470);
        this.Controls.Add(this.pad);
        this.Font = ReplayWorkbench.App.Theme.Sans;
        this.ForeColor = ReplayWorkbench.App.Theme.Ink;
        this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
        this.KeyPreview = true;
        this.MaximizeBox = false;
        this.MinimizeBox = false;
        this.Name = "DevMenuForm";
        this.StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
        this.Text = "Register opcodes";
        this.pad.ResumeLayout(false);
        this.pad.PerformLayout();
        this.ResumeLayout(false);
    }

    #endregion

    private ReplayWorkbench.App.RulePanel pad;
    private System.Windows.Forms.Label buildLabel;
    private ReplayWorkbench.App.DarkTextBox buildBox;
    private System.Windows.Forms.Label jsonLabel;
    private ReplayWorkbench.App.DarkTextBox jsonBox;
    private ReplayWorkbench.App.HintLabel hintLabel;
    private ReplayWorkbench.App.FlatButton prefillButton;
    private ReplayWorkbench.App.FlatButton cancelButton;
    private ReplayWorkbench.App.FlatButton applyButton;
}
