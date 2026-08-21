#nullable disable

namespace ReplayWorkbench.App;

partial class MainForm
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
        components = new System.ComponentModel.Container();
        menu = new DarkMenuStrip();
        fileMenu = new ToolStripMenuItem();
        openItem = new ToolStripMenuItem();
        fileSeparator = new ToolStripSeparator();
        exitItem = new ToolStripMenuItem();
        toolsMenu = new ToolStripMenuItem();
        opcodeItem = new ToolStripMenuItem();
        initZoneItem = new ToolStripMenuItem();
        helpMenu = new ToolStripMenuItem();
        aboutItem = new ToolStripMenuItem();
        logHost = new RulePanel();
        logBox = new TextBox();
        content = new Panel();
        exportCard = new CardPanel();
        exportBody = new Panel();
        exportHint = new HintLabel();
        exportButtons = new FlowLayoutPanel();
        btnExportPull = new FlatButton();
        btnExportFull = new FlatButton();
        optionsTable = new TableLayoutPanel();
        optWaymarks = new OptionCheck();
        optNames = new OptionCheck();
        optCountdown = new OptionCheck();
        optTranspose = new OptionCheck();
        optAnon = new OptionCheck();
        optStrip = new OptionCheck();
        patchRow = new FlowLayoutPanel();
        patchLabel = new Label();
        patchPick = new DarkComboBox();
        raceRow = new FlowLayoutPanel();
        raceLabel = new Label();
        racePick = new DarkComboBox();
        playersCard = new CardPanel();
        playersBody = new Panel();
        playerList = new Panel();
        playersButtonRow = new Panel();
        btnAnonNames = new FlatButton();
        pullsCard = new CardPanel();
        pullsBody = new Panel();
        pullList = new DarkListView();
        colNumber = new ColumnHeader();
        colChapter = new ColumnHeader();
        colAt = new ColumnHeader();
        colLength = new ColumnHeader();
        colCombat = new ColumnHeader();
        colRespawn = new ColumnHeader();
        timelineCard = new CardPanel();
        timeline = new TimelineControl();
        headerCard = new CardPanel();
        readout = new ReadoutView();
        dropHint = new DropHintLabel();
        rowTips = new ToolTip(components);
        menu.SuspendLayout();
        logHost.SuspendLayout();
        content.SuspendLayout();
        exportCard.SuspendLayout();
        exportBody.SuspendLayout();
        exportButtons.SuspendLayout();
        optionsTable.SuspendLayout();
        patchRow.SuspendLayout();
        raceRow.SuspendLayout();
        playersCard.SuspendLayout();
        playersBody.SuspendLayout();
        playersButtonRow.SuspendLayout();
        pullsCard.SuspendLayout();
        pullsBody.SuspendLayout();
        timelineCard.SuspendLayout();
        headerCard.SuspendLayout();
        SuspendLayout();
        // 
        // menu
        // 
        menu.BackColor = Color.FromArgb(27, 37, 49);
        menu.ForeColor = Color.FromArgb(214, 226, 240);
        menu.ImageScalingSize = new Size(24, 24);
        menu.Items.AddRange(new ToolStripItem[] { fileMenu, toolsMenu, helpMenu });
        menu.Location = new Point(0, 0);
        menu.Name = "menu";
        menu.Padding = new Padding(6, 2, 0, 2);
        menu.Size = new Size(1060, 36);
        menu.TabIndex = 0;
        // 
        // fileMenu
        // 
        fileMenu.BackColor = Color.FromArgb(27, 37, 49);
        fileMenu.DropDownItems.AddRange(new ToolStripItem[] { openItem, fileSeparator, exitItem });
        fileMenu.ForeColor = Color.FromArgb(214, 226, 240);
        fileMenu.Name = "fileMenu";
        fileMenu.Size = new Size(54, 32);
        fileMenu.Text = "&File";
        // 
        // openItem
        // 
        openItem.BackColor = Color.FromArgb(27, 37, 49);
        openItem.ForeColor = Color.FromArgb(214, 226, 240);
        openItem.Name = "openItem";
        openItem.ShortcutKeys = Keys.Control | Keys.O;
        openItem.Size = new Size(317, 34);
        openItem.Text = "&Open recording…";
        openItem.Click += OnOpenClick;
        // 
        // fileSeparator
        // 
        fileSeparator.Name = "fileSeparator";
        fileSeparator.Size = new Size(314, 6);
        // 
        // exitItem
        // 
        exitItem.BackColor = Color.FromArgb(27, 37, 49);
        exitItem.ForeColor = Color.FromArgb(214, 226, 240);
        exitItem.Name = "exitItem";
        exitItem.ShortcutKeys = Keys.Alt | Keys.F4;
        exitItem.Size = new Size(317, 34);
        exitItem.Text = "E&xit";
        exitItem.Click += OnExitClick;
        // 
        // toolsMenu
        // 
        toolsMenu.BackColor = Color.FromArgb(27, 37, 49);
        toolsMenu.DropDownItems.AddRange(new ToolStripItem[] { opcodeItem, initZoneItem });
        toolsMenu.ForeColor = Color.FromArgb(214, 226, 240);
        toolsMenu.Name = "toolsMenu";
        toolsMenu.Size = new Size(69, 32);
        toolsMenu.Text = "&Tools";
        // 
        // opcodeItem
        // 
        opcodeItem.BackColor = Color.FromArgb(27, 37, 49);
        opcodeItem.ForeColor = Color.FromArgb(214, 226, 240);
        opcodeItem.Name = "opcodeItem";
        opcodeItem.Size = new Size(300, 34);
        opcodeItem.Text = "&Register opcode table…";
        opcodeItem.Click += OnRegisterOpcodesClick;
        // 
        // initZoneItem
        // 
        initZoneItem.BackColor = Color.FromArgb(27, 37, 49);
        initZoneItem.ForeColor = Color.FromArgb(214, 226, 240);
        initZoneItem.Name = "initZoneItem";
        initZoneItem.Size = new Size(300, 34);
        initZoneItem.Text = "Set &InitZone template…";
        initZoneItem.Click += OnInitZoneTemplateClick;
        // 
        // helpMenu
        // 
        helpMenu.BackColor = Color.FromArgb(27, 37, 49);
        helpMenu.DropDownItems.AddRange(new ToolStripItem[] { aboutItem });
        helpMenu.ForeColor = Color.FromArgb(214, 226, 240);
        helpMenu.Name = "helpMenu";
        helpMenu.Size = new Size(65, 32);
        helpMenu.Text = "&Help";
        // 
        // aboutItem
        // 
        aboutItem.BackColor = Color.FromArgb(27, 37, 49);
        aboutItem.ForeColor = Color.FromArgb(214, 226, 240);
        aboutItem.Name = "aboutItem";
        aboutItem.Size = new Size(164, 34);
        aboutItem.Text = "&About";
        aboutItem.Click += OnAboutClick;
        // 
        // logHost
        // 
        logHost.BackColor = Color.FromArgb(20, 27, 36);
        logHost.Controls.Add(logBox);
        logHost.Dock = DockStyle.Bottom;
        logHost.ForeColor = Color.FromArgb(214, 226, 240);
        logHost.Location = new Point(0, 804);
        logHost.Name = "logHost";
        logHost.Padding = new Padding(12, 8, 12, 8);
        logHost.Size = new Size(1060, 96);
        logHost.TabIndex = 2;
        // 
        // logBox
        // 
        logBox.BackColor = Color.FromArgb(20, 27, 36);
        logBox.BorderStyle = BorderStyle.None;
        logBox.Dock = DockStyle.Fill;
        logBox.Font = new Font("Consolas", 8F);
        logBox.ForeColor = Color.FromArgb(125, 141, 160);
        logBox.Location = new Point(12, 8);
        logBox.Multiline = true;
        logBox.Name = "logBox";
        logBox.ReadOnly = true;
        logBox.ScrollBars = ScrollBars.Vertical;
        logBox.Size = new Size(1036, 80);
        logBox.TabIndex = 0;
        logBox.TabStop = false;
        // 
        // content
        // 
        content.AutoScroll = true;
        content.BackColor = Color.FromArgb(13, 17, 23);
        content.Controls.Add(exportCard);
        content.Controls.Add(playersCard);
        content.Controls.Add(pullsCard);
        content.Controls.Add(timelineCard);
        content.Controls.Add(headerCard);
        content.Controls.Add(dropHint);
        content.Dock = DockStyle.Fill;
        content.Location = new Point(0, 36);
        content.Name = "content";
        content.Padding = new Padding(20, 12, 20, 20);
        content.Size = new Size(1060, 768);
        content.TabIndex = 1;
        // 
        // exportCard
        // 
        exportCard.AutoSizeToContent = true;
        exportCard.BackColor = Color.FromArgb(20, 27, 36);
        exportCard.Controls.Add(exportBody);
        exportCard.Dock = DockStyle.Top;
        exportCard.ForeColor = Color.FromArgb(214, 226, 240);
        exportCard.Location = new Point(20, 1106);
        exportCard.Meta = "Single pull → .dat";
        exportCard.Name = "exportCard";
        exportCard.Padding = new Padding(1, 30, 1, 1);
        exportCard.Size = new Size(994, 329);
        exportCard.TabIndex = 5;
        exportCard.Title = "Export";
        // 
        // exportBody
        // 
        exportBody.AutoSize = true;
        exportBody.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        exportBody.BackColor = Color.FromArgb(20, 27, 36);
        exportBody.Controls.Add(exportHint);
        exportBody.Controls.Add(exportButtons);
        exportBody.Controls.Add(optionsTable);
        exportBody.Dock = DockStyle.Top;
        exportBody.Location = new Point(1, 30);
        exportBody.Name = "exportBody";
        exportBody.Padding = new Padding(14, 12, 14, 14);
        exportBody.Size = new Size(992, 298);
        exportBody.TabIndex = 0;
        // 
        // exportHint
        // 
        exportHint.AutoSize = true;
        exportHint.BackColor = Color.FromArgb(20, 27, 36);
        exportHint.Dock = DockStyle.Top;
        exportHint.Font = new Font("Consolas", 8F);
        exportHint.ForeColor = Color.FromArgb(125, 141, 160);
        exportHint.Location = new Point(14, 265);
        exportHint.Name = "exportHint";
        exportHint.Size = new Size(531, 19);
        exportHint.TabIndex = 2;
        exportHint.Text = "Select a pull from the timeline or table to enable export.";
        exportHint.UseMnemonic = false;
        // 
        // exportButtons
        // 
        exportButtons.AutoSize = true;
        exportButtons.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        exportButtons.Controls.Add(btnExportPull);
        exportButtons.Controls.Add(btnExportFull);
        exportButtons.Dock = DockStyle.Top;
        exportButtons.Location = new Point(14, 216);
        exportButtons.Name = "exportButtons";
        exportButtons.Padding = new Padding(0, 8, 0, 0);
        exportButtons.Size = new Size(964, 49);
        exportButtons.TabIndex = 1;
        exportButtons.WrapContents = false;
        // 
        // btnExportPull
        // 
        btnExportPull.Accent = true;
        btnExportPull.BackColor = Color.FromArgb(24, 52, 56);
        btnExportPull.Enabled = false;
        btnExportPull.FlatAppearance.BorderColor = Color.FromArgb(28, 111, 106);
        btnExportPull.FlatAppearance.MouseDownBackColor = Color.FromArgb(27, 37, 49);
        btnExportPull.FlatAppearance.MouseOverBackColor = Color.FromArgb(28, 111, 106);
        btnExportPull.FlatStyle = FlatStyle.Flat;
        btnExportPull.Font = new Font("Segoe UI", 9F);
        btnExportPull.ForeColor = Color.FromArgb(57, 212, 200);
        btnExportPull.Location = new Point(0, 8);
        btnExportPull.Margin = new Padding(0, 0, 12, 0);
        btnExportPull.Name = "btnExportPull";
        btnExportPull.Size = new Size(198, 41);
        btnExportPull.TabIndex = 0;
        btnExportPull.Text = "Export selected pull";
        btnExportPull.UseVisualStyleBackColor = false;
        btnExportPull.Click += OnExportPullClick;
        // 
        // btnExportFull
        // 
        btnExportFull.BackColor = Color.FromArgb(20, 27, 36);
        btnExportFull.FlatAppearance.BorderColor = Color.FromArgb(38, 51, 68);
        btnExportFull.FlatAppearance.MouseDownBackColor = Color.FromArgb(27, 37, 49);
        btnExportFull.FlatAppearance.MouseOverBackColor = Color.FromArgb(27, 37, 49);
        btnExportFull.FlatStyle = FlatStyle.Flat;
        btnExportFull.Font = new Font("Segoe UI", 9F);
        btnExportFull.ForeColor = Color.FromArgb(214, 226, 240);
        btnExportFull.Location = new Point(210, 8);
        btnExportFull.Margin = new Padding(0, 0, 12, 0);
        btnExportFull.Name = "btnExportFull";
        btnExportFull.Size = new Size(227, 41);
        btnExportFull.TabIndex = 1;
        btnExportFull.Text = "Export renamed full file";
        btnExportFull.UseVisualStyleBackColor = false;
        btnExportFull.Click += OnExportFullClick;
        // 
        // optionsTable
        // 
        optionsTable.AutoSize = true;
        optionsTable.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        optionsTable.BackColor = Color.FromArgb(20, 27, 36);
        optionsTable.ColumnCount = 2;
        optionsTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        optionsTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        optionsTable.Controls.Add(optWaymarks, 0, 0);
        optionsTable.Controls.Add(optNames, 1, 0);
        optionsTable.Controls.Add(optCountdown, 0, 1);
        optionsTable.Controls.Add(optTranspose, 1, 1);
        optionsTable.Controls.Add(optAnon, 0, 2);
        optionsTable.Controls.Add(optStrip, 1, 2);
        optionsTable.Controls.Add(patchRow, 0, 3);
        optionsTable.Controls.Add(raceRow, 1, 3);
        optionsTable.Dock = DockStyle.Top;
        optionsTable.Location = new Point(14, 12);
        optionsTable.Name = "optionsTable";
        optionsTable.RowCount = 4;
        optionsTable.RowStyles.Add(new RowStyle());
        optionsTable.RowStyles.Add(new RowStyle());
        optionsTable.RowStyles.Add(new RowStyle());
        optionsTable.RowStyles.Add(new RowStyle());
        optionsTable.Size = new Size(964, 204);
        optionsTable.TabIndex = 0;
        // 
        // optWaymarks
        // 
        optWaymarks.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        optWaymarks.BackColor = Color.FromArgb(20, 27, 36);
        optWaymarks.Caption = "Carry waymarks";
        optWaymarks.Checked = true;
        optWaymarks.Location = new Point(0, 0);
        optWaymarks.Margin = new Padding(0, 0, 24, 0);
        optWaymarks.Name = "optWaymarks";
        optWaymarks.Size = new Size(458, 55);
        optWaymarks.SubText = "Carry the last waymarks into the pull";
        optWaymarks.TabIndex = 0;
        // 
        // optNames
        // 
        optNames.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        optNames.BackColor = Color.FromArgb(20, 27, 36);
        optNames.Caption = "Apply name edits";
        optNames.Checked = true;
        optNames.Location = new Point(482, 0);
        optNames.Margin = new Padding(0, 0, 24, 0);
        optNames.Name = "optNames";
        optNames.Size = new Size(458, 55);
        optNames.SubText = "Write the names typed above into the export";
        optNames.TabIndex = 1;
        // 
        // optCountdown
        // 
        optCountdown.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        optCountdown.BackColor = Color.FromArgb(20, 27, 36);
        optCountdown.Caption = "Keep engage chapter";
        optCountdown.Checked = true;
        optCountdown.Location = new Point(0, 55);
        optCountdown.Margin = new Padding(0, 0, 24, 0);
        optCountdown.Name = "optCountdown";
        optCountdown.Size = new Size(458, 55);
        optCountdown.SubText = "Expose the countdown/engage as a second chapter";
        optCountdown.TabIndex = 2;
        // 
        // optTranspose
        // 
        optTranspose.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        optTranspose.BackColor = Color.FromArgb(20, 27, 36);
        optTranspose.Caption = "Transpose opcodes";
        optTranspose.Location = new Point(482, 55);
        optTranspose.Margin = new Padding(0, 0, 24, 0);
        optTranspose.Name = "optTranspose";
        optTranspose.Size = new Size(458, 55);
        optTranspose.SubText = "Remap packets so the current client reads them";
        optTranspose.TabIndex = 3;
        // 
        // optAnon
        // 
        optAnon.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        optAnon.BackColor = Color.FromArgb(20, 27, 36);
        optAnon.Caption = "Anonymize players";
        optAnon.Location = new Point(0, 110);
        optAnon.Margin = new Padding(0, 0, 24, 0);
        optAnon.Name = "optAnon";
        optAnon.Size = new Size(458, 55);
        optAnon.SubText = "Replace names, object IDs, character keys, race, gear & weapons so no one is identifiable";
        optAnon.TabIndex = 4;
        // 
        // optStrip
        // 
        optStrip.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        optStrip.BackColor = Color.FromArgb(20, 27, 36);
        optStrip.Caption = "Strip party portraits";
        optStrip.Location = new Point(482, 110);
        optStrip.Margin = new Padding(0, 0, 24, 0);
        optStrip.Name = "optStrip";
        optStrip.Size = new Size(458, 55);
        optStrip.SubText = "Delete the PartyPortraitInfo packets entirely";
        optStrip.TabIndex = 5;
        // 
        // patchRow
        // 
        patchRow.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        patchRow.AutoSize = true;
        patchRow.BackColor = Color.FromArgb(20, 27, 36);
        patchRow.Controls.Add(patchLabel);
        patchRow.Controls.Add(patchPick);
        patchRow.Location = new Point(3, 168);
        patchRow.Name = "patchRow";
        patchRow.Padding = new Padding(0, 3, 0, 0);
        patchRow.Size = new Size(476, 33);
        patchRow.TabIndex = 6;
        patchRow.WrapContents = false;
        // 
        // patchLabel
        // 
        patchLabel.AutoSize = true;
        patchLabel.Font = new Font("Segoe UI", 9F);
        patchLabel.ForeColor = Color.FromArgb(214, 226, 240);
        patchLabel.Location = new Point(0, 8);
        patchLabel.Margin = new Padding(0, 5, 10, 0);
        patchLabel.Name = "patchLabel";
        patchLabel.Size = new Size(113, 25);
        patchLabel.TabIndex = 0;
        patchLabel.Text = "Recorded on";
        // 
        // patchPick
        // 
        patchPick.BackColor = Color.FromArgb(27, 37, 49);
        patchPick.DropDownStyle = ComboBoxStyle.DropDownList;
        patchPick.Enabled = false;
        patchPick.FlatStyle = FlatStyle.Flat;
        patchPick.Font = new Font("Consolas", 9F);
        patchPick.ForeColor = Color.FromArgb(214, 226, 240);
        patchPick.Location = new Point(123, 3);
        patchPick.Margin = new Padding(0);
        patchPick.Name = "patchPick";
        patchPick.Size = new Size(150, 30);
        patchPick.TabIndex = 1;
        patchPick.SelectedIndexChanged += OnPatchPicked;
        // 
        // raceRow
        // 
        raceRow.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        raceRow.AutoSize = true;
        raceRow.BackColor = Color.FromArgb(20, 27, 36);
        raceRow.Controls.Add(raceLabel);
        raceRow.Controls.Add(racePick);
        raceRow.Location = new Point(485, 168);
        raceRow.Name = "raceRow";
        raceRow.Padding = new Padding(0, 3, 0, 0);
        raceRow.Size = new Size(476, 33);
        raceRow.TabIndex = 7;
        raceRow.WrapContents = false;
        // 
        // raceLabel
        // 
        raceLabel.AutoSize = true;
        raceLabel.Font = new Font("Segoe UI", 9F);
        raceLabel.ForeColor = Color.FromArgb(214, 226, 240);
        raceLabel.Location = new Point(0, 8);
        raceLabel.Margin = new Padding(0, 5, 10, 0);
        raceLabel.Name = "raceLabel";
        raceLabel.Size = new Size(49, 25);
        raceLabel.TabIndex = 0;
        raceLabel.Text = "Race";
        // 
        // racePick
        // 
        racePick.BackColor = Color.FromArgb(27, 37, 49);
        racePick.DropDownStyle = ComboBoxStyle.DropDownList;
        racePick.Enabled = false;
        racePick.FlatStyle = FlatStyle.Flat;
        racePick.Font = new Font("Consolas", 9F);
        racePick.ForeColor = Color.FromArgb(214, 226, 240);
        racePick.Location = new Point(59, 3);
        racePick.Margin = new Padding(0);
        racePick.Name = "racePick";
        racePick.Size = new Size(150, 30);
        racePick.TabIndex = 1;
        // 
        // playersCard
        // 
        playersCard.BackColor = Color.FromArgb(20, 27, 36);
        playersCard.Controls.Add(playersBody);
        playersCard.Dock = DockStyle.Top;
        playersCard.ForeColor = Color.FromArgb(214, 226, 240);
        playersCard.GapBelow = 14;
        playersCard.Location = new Point(20, 740);
        playersCard.Name = "playersCard";
        playersCard.Padding = new Padding(1, 30, 1, 1);
        playersCard.Size = new Size(994, 366);
        playersCard.TabIndex = 4;
        playersCard.Title = "Players";
        // 
        // playersBody
        // 
        playersBody.BackColor = Color.FromArgb(20, 27, 36);
        playersBody.Controls.Add(playerList);
        playersBody.Controls.Add(playersButtonRow);
        playersBody.Dock = DockStyle.Fill;
        playersBody.Location = new Point(1, 30);
        playersBody.Name = "playersBody";
        playersBody.Padding = new Padding(14, 10, 14, 12);
        playersBody.Size = new Size(992, 321);
        playersBody.TabIndex = 0;
        // 
        // playerList
        // 
        playerList.AutoScroll = true;
        playerList.BackColor = Color.FromArgb(20, 27, 36);
        playerList.Dock = DockStyle.Fill;
        playerList.Location = new Point(14, 48);
        playerList.Name = "playerList";
        playerList.Size = new Size(964, 261);
        playerList.TabIndex = 1;
        // 
        // playersButtonRow
        // 
        playersButtonRow.BackColor = Color.FromArgb(20, 27, 36);
        playersButtonRow.Controls.Add(btnAnonNames);
        playersButtonRow.Dock = DockStyle.Top;
        playersButtonRow.Location = new Point(14, 10);
        playersButtonRow.Name = "playersButtonRow";
        playersButtonRow.Size = new Size(964, 38);
        playersButtonRow.TabIndex = 0;
        // 
        // btnAnonNames
        // 
        btnAnonNames.BackColor = Color.FromArgb(20, 27, 36);
        btnAnonNames.FlatAppearance.BorderColor = Color.FromArgb(38, 51, 68);
        btnAnonNames.FlatAppearance.MouseDownBackColor = Color.FromArgb(27, 37, 49);
        btnAnonNames.FlatAppearance.MouseOverBackColor = Color.FromArgb(27, 37, 49);
        btnAnonNames.FlatStyle = FlatStyle.Flat;
        btnAnonNames.Font = new Font("Segoe UI", 9F);
        btnAnonNames.ForeColor = Color.FromArgb(214, 226, 240);
        btnAnonNames.Location = new Point(0, 0);
        btnAnonNames.Margin = new Padding(0, 0, 12, 0);
        btnAnonNames.Name = "btnAnonNames";
        btnAnonNames.Size = new Size(212, 41);
        btnAnonNames.TabIndex = 0;
        btnAnonNames.Text = "Anonymize all names";
        btnAnonNames.UseVisualStyleBackColor = false;
        btnAnonNames.Click += OnAnonymizeNamesClick;
        // 
        // pullsCard
        // 
        pullsCard.BackColor = Color.FromArgb(20, 27, 36);
        pullsCard.Controls.Add(pullsBody);
        pullsCard.Dock = DockStyle.Top;
        pullsCard.ForeColor = Color.FromArgb(214, 226, 240);
        pullsCard.GapBelow = 14;
        pullsCard.Location = new Point(20, 418);
        pullsCard.Name = "pullsCard";
        pullsCard.Padding = new Padding(1, 30, 1, 1);
        pullsCard.Size = new Size(994, 322);
        pullsCard.TabIndex = 3;
        pullsCard.Title = "Pulls";
        // 
        // pullsBody
        // 
        pullsBody.BackColor = Color.FromArgb(20, 27, 36);
        pullsBody.Controls.Add(pullList);
        pullsBody.Dock = DockStyle.Fill;
        pullsBody.Location = new Point(1, 30);
        pullsBody.Name = "pullsBody";
        pullsBody.Padding = new Padding(1, 0, 1, 8);
        pullsBody.Size = new Size(992, 277);
        pullsBody.TabIndex = 0;
        // 
        // pullList
        // 
        pullList.BackColor = Color.FromArgb(20, 27, 36);
        pullList.BorderStyle = BorderStyle.None;
        pullList.Columns.AddRange(new ColumnHeader[] { colNumber, colChapter, colAt, colLength, colCombat, colRespawn });
        pullList.Dock = DockStyle.Fill;
        pullList.Font = new Font("Consolas", 9F);
        pullList.ForeColor = Color.FromArgb(214, 226, 240);
        pullList.FullRowSelect = true;
        pullList.Location = new Point(1, 0);
        pullList.MultiSelect = false;
        pullList.Name = "pullList";
        pullList.OwnerDraw = true;
        pullList.Size = new Size(990, 269);
        pullList.TabIndex = 0;
        pullList.UseCompatibleStateImageBehavior = false;
        pullList.View = View.Details;
        pullList.SelectedIndexChanged += OnPullListSelectionChanged;
        // 
        // colNumber
        // 
        colNumber.Text = "#";
        colNumber.TextAlign = HorizontalAlignment.Right;
        colNumber.Width = 44;
        // 
        // colChapter
        // 
        colChapter.Text = "chapter";
        colChapter.Width = 240;
        // 
        // colAt
        // 
        colAt.Text = "at";
        colAt.Width = 112;
        // 
        // colLength
        // 
        colLength.Text = "length";
        colLength.Width = 112;
        // 
        // colCombat
        // 
        colCombat.Text = "combat";
        colCombat.TextAlign = HorizontalAlignment.Right;
        colCombat.Width = 112;
        // 
        // colRespawn
        // 
        colRespawn.Text = "respawn batch";
        colRespawn.TextAlign = HorizontalAlignment.Right;
        colRespawn.Width = 124;
        // 
        // timelineCard
        // 
        timelineCard.AutoSizeToContent = true;
        timelineCard.BackColor = Color.FromArgb(20, 27, 36);
        timelineCard.Controls.Add(timeline);
        timelineCard.Dock = DockStyle.Top;
        timelineCard.ForeColor = Color.FromArgb(214, 226, 240);
        timelineCard.GapBelow = 14;
        timelineCard.Location = new Point(20, 303);
        timelineCard.Name = "timelineCard";
        timelineCard.Padding = new Padding(1, 30, 1, 1);
        timelineCard.Size = new Size(994, 115);
        timelineCard.TabIndex = 2;
        timelineCard.Title = "Pull Timeline";
        // 
        // timeline
        // 
        timeline.BackColor = Color.FromArgb(20, 27, 36);
        timeline.Dock = DockStyle.Top;
        timeline.Location = new Point(1, 30);
        timeline.Name = "timeline";
        timeline.Size = new Size(992, 70);
        timeline.TabIndex = 0;
        timeline.PullSelected += OnTimelinePullSelected;
        // 
        // headerCard
        // 
        headerCard.AutoSizeToContent = true;
        headerCard.BackColor = Color.FromArgb(20, 27, 36);
        headerCard.Controls.Add(readout);
        headerCard.Dock = DockStyle.Top;
        headerCard.ForeColor = Color.FromArgb(214, 226, 240);
        headerCard.GapBelow = 14;
        headerCard.Location = new Point(20, 120);
        headerCard.Name = "headerCard";
        headerCard.Padding = new Padding(1, 30, 1, 1);
        headerCard.Size = new Size(994, 183);
        headerCard.TabIndex = 1;
        headerCard.Title = "Recording Header";
        // 
        // readout
        // 
        readout.BackColor = Color.FromArgb(20, 27, 36);
        readout.Dock = DockStyle.Top;
        readout.Location = new Point(1, 30);
        readout.Name = "readout";
        readout.Size = new Size(992, 138);
        readout.TabIndex = 0;
        // 
        // dropHint
        // 
        dropHint.BackColor = Color.FromArgb(13, 17, 23);
        dropHint.Dock = DockStyle.Top;
        dropHint.Font = new Font("Consolas", 9F);
        dropHint.ForeColor = Color.FromArgb(125, 141, 160);
        dropHint.GapBelow = 16;
        dropHint.Location = new Point(20, 12);
        dropHint.Name = "dropHint";
        dropHint.Padding = new Padding(0, 0, 0, 16);
        dropHint.Size = new Size(994, 108);
        dropHint.TabIndex = 0;
        dropHint.Text = "Drop a duty recording here, or use File ▸ Open…";
        dropHint.TextAlign = ContentAlignment.MiddleCenter;
        dropHint.Click += OnDropHintClick;
        // 
        // rowTips
        // 
        rowTips.AutoPopDelay = 12000;
        rowTips.InitialDelay = 350;
        rowTips.ReshowDelay = 100;
        // 
        // MainForm
        // 
        AllowDrop = true;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.FromArgb(13, 17, 23);
        ClientSize = new Size(1060, 900);
        Controls.Add(content);
        Controls.Add(logHost);
        Controls.Add(menu);
        Font = new Font("Segoe UI", 9F);
        ForeColor = Color.FromArgb(214, 226, 240);
        KeyPreview = true;
        MainMenuStrip = menu;
        MinimumSize = new Size(880, 640);
        Name = "MainForm";
        StartPosition = FormStartPosition.CenterScreen;
        Text = "Replay Workbench";
        menu.ResumeLayout(false);
        menu.PerformLayout();
        logHost.ResumeLayout(false);
        logHost.PerformLayout();
        content.ResumeLayout(false);
        exportCard.ResumeLayout(false);
        exportCard.PerformLayout();
        exportBody.ResumeLayout(false);
        exportBody.PerformLayout();
        exportButtons.ResumeLayout(false);
        optionsTable.ResumeLayout(false);
        optionsTable.PerformLayout();
        patchRow.ResumeLayout(false);
        patchRow.PerformLayout();
        raceRow.ResumeLayout(false);
        raceRow.PerformLayout();
        playersCard.ResumeLayout(false);
        playersBody.ResumeLayout(false);
        playersButtonRow.ResumeLayout(false);
        pullsCard.ResumeLayout(false);
        pullsBody.ResumeLayout(false);
        timelineCard.ResumeLayout(false);
        headerCard.ResumeLayout(false);
        ResumeLayout(false);
        PerformLayout();
    }

    #endregion

    private ReplayWorkbench.App.DarkMenuStrip menu;
    private System.Windows.Forms.ToolStripMenuItem fileMenu;
    private System.Windows.Forms.ToolStripMenuItem openItem;
    private System.Windows.Forms.ToolStripSeparator fileSeparator;
    private System.Windows.Forms.ToolStripMenuItem exitItem;
    private System.Windows.Forms.ToolStripMenuItem toolsMenu;
    private System.Windows.Forms.ToolStripMenuItem opcodeItem;
    private System.Windows.Forms.ToolStripMenuItem initZoneItem;
    private System.Windows.Forms.ToolStripMenuItem helpMenu;
    private System.Windows.Forms.ToolStripMenuItem aboutItem;
    private ReplayWorkbench.App.RulePanel logHost;
    private System.Windows.Forms.TextBox logBox;
    private System.Windows.Forms.Panel content;
    private ReplayWorkbench.App.DropHintLabel dropHint;
    private ReplayWorkbench.App.CardPanel headerCard;
    private ReplayWorkbench.App.ReadoutView readout;
    private ReplayWorkbench.App.CardPanel timelineCard;
    private ReplayWorkbench.App.TimelineControl timeline;
    private ReplayWorkbench.App.CardPanel pullsCard;
    private System.Windows.Forms.Panel pullsBody;
    private ReplayWorkbench.App.DarkListView pullList;
    private System.Windows.Forms.ColumnHeader colNumber;
    private System.Windows.Forms.ColumnHeader colChapter;
    private System.Windows.Forms.ColumnHeader colAt;
    private System.Windows.Forms.ColumnHeader colLength;
    private System.Windows.Forms.ColumnHeader colCombat;
    private System.Windows.Forms.ColumnHeader colRespawn;
    private ReplayWorkbench.App.CardPanel playersCard;
    private System.Windows.Forms.Panel playersBody;
    private System.Windows.Forms.Panel playersButtonRow;
    private ReplayWorkbench.App.FlatButton btnAnonNames;
    private System.Windows.Forms.Panel playerList;
    private ReplayWorkbench.App.CardPanel exportCard;
    private System.Windows.Forms.Panel exportBody;
    private System.Windows.Forms.TableLayoutPanel optionsTable;
    private ReplayWorkbench.App.OptionCheck optWaymarks;
    private ReplayWorkbench.App.OptionCheck optNames;
    private ReplayWorkbench.App.OptionCheck optCountdown;
    private ReplayWorkbench.App.OptionCheck optTranspose;
    private ReplayWorkbench.App.OptionCheck optAnon;
    private ReplayWorkbench.App.OptionCheck optStrip;
    private System.Windows.Forms.FlowLayoutPanel patchRow;
    private System.Windows.Forms.Label patchLabel;
    private ReplayWorkbench.App.DarkComboBox patchPick;
    private System.Windows.Forms.FlowLayoutPanel raceRow;
    private System.Windows.Forms.Label raceLabel;
    private ReplayWorkbench.App.DarkComboBox racePick;
    private System.Windows.Forms.FlowLayoutPanel exportButtons;
    private ReplayWorkbench.App.FlatButton btnExportPull;
    private ReplayWorkbench.App.FlatButton btnExportFull;
    private ReplayWorkbench.App.HintLabel exportHint;
    private System.Windows.Forms.ToolTip rowTips;
}
