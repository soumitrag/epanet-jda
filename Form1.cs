namespace Demand_Gen;

// Owns user interaction and presents results; network parsing and demand logic stay in separate services.
public partial class Form1 : Form
{
    private const int TitleBarHeight = 32;
    private const int TitleBarButtonWidth = 46;
    private const int WindowWidth = 1000;
    private const int WindowHeight = 660 + TitleBarHeight;
    private const int DesignWindowWidth = 1120;
    private const int VerticalScrollBar = 1;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowScrollBar(IntPtr handle, int bar, bool show);

    private readonly TextBox _inputPathTextBox = new();
    private readonly TextBox _totalDemandTextBox = new();
    private readonly ComboBox _methodComboBox = new();
    private readonly ComboBox _unitComboBox = new();
    private readonly NumericUpDown _zoneCountSpinner = new();
    private readonly DataGridView _zoneGrid = new();
    private readonly TextBox _validationReportTextBox = new();
    private readonly Button _calculateButton = new();
    private readonly Button _saveSessionButton = new();
    private readonly Button _loadSessionButton = new();
    private readonly Label _validationMessageLabel = new();
    private readonly Label _hoverHelpLabel = new();
    private readonly Label _statusLabel = new();
    private EpanetNetworkSummary? _network;
    private DemandValidationResult? _validationResult;
    private bool _hasUnsavedChanges;
    private bool _allowClose;

    public Form1()
    {
        InitializeComponent();
        ConfigureWindow();
        BuildControls();
        // Keep the report scrollbar visible even when the initial report is empty.
        Shown += (_, _) => EnsureValidationReportScrollBarVisible();
    }

    // Apply the application's borderless-window settings and attach close-confirmation behavior.
    private void ConfigureWindow()
    {
        var version = typeof(Form1).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
        Text = $"EPANET-JDA - Version {version}";
        AutoScaleMode = AutoScaleMode.None;
        Font = new Font("Segoe UI", 12, FontStyle.Regular, GraphicsUnit.Pixel);
        FormBorderStyle = FormBorderStyle.None;
        ClientSize = new Size(WindowWidth, WindowHeight);
        ControlBox = false;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowIcon = false;
        StartPosition = FormStartPosition.CenterScreen;
        FormClosing += Form1_FormClosing;
    }

    private void BuildControls()
    {
        // The title bar is separate from the scrolling form content and uses custom window buttons.
        var contentPanel = new Panel
        {
            Location = new Point(0, TitleBarHeight),
            Size = new Size(ClientSize.Width, ClientSize.Height - TitleBarHeight),
            AutoScroll = false
        };
        var titleBarPanel = new Panel
        {
            BackColor = SystemColors.Control,
            Location = Point.Empty,
            Size = new Size(ClientSize.Width, TitleBarHeight)
        };
        var windowTitleLabel = new Label
        {
            AutoSize = false,
            Location = new Point(24, 0),
            Size = new Size(ClientSize.Width - 24 - 3 * TitleBarButtonWidth, TitleBarHeight),
            Text = this.Text,
            TextAlign = ContentAlignment.MiddleLeft
        };
        windowTitleLabel.MouseDown += TitleBar_MouseDown;
        titleBarPanel.MouseDown += TitleBar_MouseDown;

        var helpWindowButton = CreateTitleBarButton("?", ClientSize.Width - 3 * TitleBarButtonWidth);
        helpWindowButton.AccessibleName = "Open help";
        helpWindowButton.Click += HelpButton_Click;
        AddHoverHelp(helpWindowButton, "Open the application guide.");

        var minimizeWindowButton = CreateTitleBarButton(string.Empty, ClientSize.Width - 2 * TitleBarButtonWidth);
        minimizeWindowButton.AccessibleName = "Minimize window";
        minimizeWindowButton.ForeColor = SystemColors.ControlText;
        minimizeWindowButton.Paint += MinimizeWindowButton_Paint;
        minimizeWindowButton.Click += (_, _) => WindowState = FormWindowState.Minimized;
        AddHoverHelp(minimizeWindowButton, "Minimize the window.");

        var closeWindowButton = CreateTitleBarButton("X", ClientSize.Width - TitleBarButtonWidth);
        closeWindowButton.Click += (_, _) => Close();
        AddHoverHelp(closeWindowButton, "Close the demand generator.");

        titleBarPanel.Controls.AddRange([windowTitleLabel, helpWindowButton, minimizeWindowButton, closeWindowButton]);

        // The main form body starts with network selection and session controls.
        var titleLabel = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI", 20, FontStyle.Bold, GraphicsUnit.Pixel),
            Location = new Point(24, 20),
            Text = $"EPANET-JDA (EPANET Junction Demand Allocator)"
        };

        var inputLabel = new Label
        {
            AutoSize = true,
            Location = new Point(24, 64),
            Text = "Input file (.inp)"
        };

        _inputPathTextBox.Location = new Point(24, 88);
        _inputPathTextBox.ReadOnly = true;
        _inputPathTextBox.Size = new Size(650, 27);
        _inputPathTextBox.TabStop = false;
        AddHoverHelp(_inputPathTextBox, "Displays the selected EPANET 2.2 input file.");

        _loadSessionButton.Location = new Point(770, 86);
        _loadSessionButton.Size = new Size(90, 30);
        _loadSessionButton.Text = "Load";
        _loadSessionButton.UseVisualStyleBackColor = true;
        _loadSessionButton.Click += LoadSessionButton_Click;
        AddHoverHelp(_loadSessionButton, "Load a previously saved demand-generator session.");

        _saveSessionButton.Location = new Point(865, 86);
        _saveSessionButton.Size = new Size(90, 30);
        _saveSessionButton.Text = "Save";
        _saveSessionButton.UseVisualStyleBackColor = true;
        _saveSessionButton.Click += SaveSessionButton_Click;
        AddHoverHelp(_saveSessionButton, "Save the current network and demand instructions as a session.");

        var browseButton = new Button
        {
            Location = new Point(960, 86),
            Size = new Size(130, 30),
            Text = "Browse...",
            UseVisualStyleBackColor = true
        };
        browseButton.Click += BrowseButton_Click;
        AddHoverHelp(browseButton, "Select an EPANET 2.2 .inp network file.");

        var methodLabel = new Label
        {
            AutoSize = false,
            Location = new Point(24, 130),
            Size = new Size(190, 20),
            Text = "Distribution method",
            TextAlign = ContentAlignment.MiddleCenter
        };

        _methodComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _methodComboBox.Location = new Point(24, 154);
        _methodComboBox.Size = new Size(190, 28);
        _methodComboBox.Items.AddRange(["Unit-Length", "Equal-Distribution"]);
        _methodComboBox.SelectedIndex = 0;
        _methodComboBox.SelectedIndexChanged += MethodComboBox_SelectedIndexChanged;
        AddHoverHelp(_methodComboBox, "Choose how zone demand is allocated to its junctions.");

        var totalDemandLabel = new Label
        {
            AutoSize = false,
            Location = new Point(250, 130),
            Size = new Size(160, 20),
            Text = "Total demand",
            TextAlign = ContentAlignment.MiddleCenter
        };

        _totalDemandTextBox.Location = new Point(250, 154);
        _totalDemandTextBox.Size = new Size(160, 27);
        _totalDemandTextBox.TextChanged += InputFields_Changed;
        AddHoverHelp(_totalDemandTextBox, "Enter the total demand for the network in the selected flow unit.");

        var unitLabel = new Label
        {
            AutoSize = false,
            Location = new Point(450, 130),
            Size = new Size(100, 20),
            Text = "Flow unit",
            TextAlign = ContentAlignment.MiddleCenter
        };

        _unitComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _unitComboBox.Items.AddRange(["CFS", "GPM", "MGD", "IMGD", "AFD", "LPS", "LPM", "MLD", "CMH", "CMD"]);
        _unitComboBox.Location = new Point(450, 154);
        _unitComboBox.Size = new Size(100, 28);
        _unitComboBox.SelectedItem = "LPS";
        _unitComboBox.SelectedIndexChanged += InputFields_Changed;
        AddHoverHelp(_unitComboBox, "Select the flow unit; it must match the network file.");

        // The zone grid captures raw instructions; the modifier headings depend on the selected method.
        var zoneCountLabel = new Label
        {
            AutoSize = false,
            Location = new Point(540, 130),
            Size = new Size(180, 20),
            Text = "Number of zones",
            TextAlign = ContentAlignment.MiddleCenter
        };

        _zoneCountSpinner.Location = new Point(600, 154);
        _zoneCountSpinner.Minimum = 1;
        _zoneCountSpinner.Maximum = 100;
        _zoneCountSpinner.Value = 1;
        _zoneCountSpinner.Size = new Size(60, 27);
        _zoneCountSpinner.ValueChanged += ZoneCountSpinner_ValueChanged;
        AddHoverHelp(_zoneCountSpinner, "Set the number of demand zones to configure.");

        _zoneGrid.AllowUserToAddRows = false;
        _zoneGrid.AllowUserToDeleteRows = false;
        _zoneGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _zoneGrid.AllowUserToResizeColumns = true;
        _zoneGrid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        _zoneGrid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
        _zoneGrid.ColumnHeadersHeight = 56;
        _zoneGrid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        _zoneGrid.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
        _zoneGrid.Location = new Point(24, 202);
        _zoneGrid.MultiSelect = false;
        _zoneGrid.RowHeadersVisible = false;
        _zoneGrid.SelectionMode = DataGridViewSelectionMode.CellSelect;
        _zoneGrid.Size = new Size(1072, 200);
        _zoneGrid.Columns.AddRange(
        [
            CreateZoneColumn("Zone", "Zone Number", 9),
            CreateZoneColumn("Demand", "Zone Demand", 12),
            CreateZoneColumn("Junctions", "Junctions", 34),
            CreateZoneColumn("KHalfPipes", "K = 0.5 Pipes", 22),
            CreateZoneColumn("KZeroPipes", "K = 0 Pipes", 23)
        ]);
        UpdateZoneModifierColumnHeaders();
        SyncZoneRows((int)_zoneCountSpinner.Value);

        _zoneGrid.CellEndEdit += ZoneGrid_CellEndEdit;
        AddHoverHelp(_zoneGrid, "Enter each zone's demand, junctions, and method-specific modifiers.");

        _calculateButton.Enabled = false;
        _calculateButton.Location = new Point(700, 152);
        _calculateButton.Size = new Size(160, 32);
        _calculateButton.Text = "Calculate && Save";
        _calculateButton.UseVisualStyleBackColor = true;
        _calculateButton.Click += CalculateButton_Click;
        AddHoverHelp(_calculateButton, "Calculate junction demands and write a verified generated .inp copy.");

        var validateButton = new Button
        {
            Location = new Point(900, 152),
            Size = new Size(150, 32),
            Text = "Validate",
            UseVisualStyleBackColor = true
        };
        validateButton.Click += ValidateButton_Click;
        AddHoverHelp(validateButton, "Check the network and all demand instructions without writing a file.");

        var validationReportLabel = new Label
        {
            AutoSize = true,
            Location = new Point(24, 414),
            Text = "Validation report"
        };

        _validationMessageLabel.AutoEllipsis = true;
        _validationMessageLabel.Font = new Font("Segoe UI", 12, FontStyle.Bold, GraphicsUnit.Pixel);
        _validationMessageLabel.Location = new Point(180, 414);
        _validationMessageLabel.Size = new Size(916, 20);

        _validationReportTextBox.Location = new Point(24, 436);
        _validationReportTextBox.AutoSize = false;
        _validationReportTextBox.Multiline = true;
        _validationReportTextBox.ReadOnly = true;
        _validationReportTextBox.ScrollBars = ScrollBars.Vertical;
        _validationReportTextBox.Size = new Size(1072, 120);
        _validationReportTextBox.WordWrap = true;
        _validationReportTextBox.TextChanged += (_, _) => EnsureValidationReportScrollBarVisible();
        AddHoverHelp(_validationReportTextBox, "Shows validation details or the generated demand report.");

        _hoverHelpLabel.AutoEllipsis = true;
        _hoverHelpLabel.Font = new Font("Segoe UI", 12, FontStyle.Bold, GraphicsUnit.Pixel);
        _hoverHelpLabel.Location = new Point(24, 564);
        _hoverHelpLabel.Size = new Size(1072, 20);

        _statusLabel.AutoEllipsis = true;
        _statusLabel.Location = new Point(24, 588);
        _statusLabel.Size = new Size(1072, 22);
        _statusLabel.Text = "No input file selected";

        var authorLabel = new Label
        {
            AutoSize = false,
            Location = new Point(650, 620),
            Size = new Size(224, 30),
            Text = "Soumitra Ganguly",
            TextAlign = ContentAlignment.MiddleRight
        };

        var copyrightButton = new Button
        {
            Location = new Point(880, 620),
            Size = new Size(100, 30),
            Text = "Copyright",
            UseVisualStyleBackColor = true
        };
        copyrightButton.Click += CopyrightButton_Click;
        AddHoverHelp(copyrightButton, "View the application copyright information.");

        var exitButton = new Button
        {
            Location = new Point(986, 620),
            Size = new Size(110, 30),
            Text = "Exit",
            UseVisualStyleBackColor = true
        };
        exitButton.Click += (_, _) => Close();
        AddHoverHelp(exitButton, "Close the demand generator.");

        var compatibilityLabel = new Label
        {
            AutoSize = true,
            Location = new Point(24, 628),
            Text = "Works on EPANET 2.2 (Version 2.2.0)"
        };

        contentPanel.Controls.AddRange(
        [
            titleLabel,
            inputLabel,
            _inputPathTextBox,
            _loadSessionButton,
            _saveSessionButton,
            browseButton,
            methodLabel,
            _methodComboBox,
            totalDemandLabel,
            _totalDemandTextBox,
            unitLabel,
            _unitComboBox,
            zoneCountLabel,
            _zoneCountSpinner,
            _zoneGrid,
            _calculateButton,
            validateButton,
            validationReportLabel,
            _hoverHelpLabel,
            _validationReportTextBox,
            _validationMessageLabel,
            _statusLabel,
            authorLabel,
            copyrightButton,
            exitButton,
            compatibilityLabel
        ]);

        // Scale the design-time horizontal layout to the actual content width.
        var horizontalScale = (float)contentPanel.ClientSize.Width / DesignWindowWidth;
        foreach (Control control in contentPanel.Controls)
        {
            control.Location = new Point(
                (int)Math.Round(control.Left * horizontalScale),
                control.Top);
            if (!control.AutoSize)
            {
                control.Width = (int)Math.Round(control.Width * horizontalScale);
            }
        }

        Controls.Add(contentPanel);
        Controls.Add(titleBarPanel);
        Controls.AddRange(
        [
            CreateBorderEdge(new Point(0, 0), new Size(ClientSize.Width, 1)),
            CreateBorderEdge(new Point(0, ClientSize.Height - 1), new Size(ClientSize.Width, 1)),
            CreateBorderEdge(new Point(0, 1), new Size(1, ClientSize.Height - 2)),
            CreateBorderEdge(new Point(ClientSize.Width - 1, 1), new Size(1, ClientSize.Height - 2))
        ]);
    }

    private static Panel CreateBorderEdge(Point location, Size size) => new()
    {
        BackColor = Color.DarkMagenta,
        Enabled = false,
        Location = location,
        Size = size,
        TabStop = false
    };

    private void EnsureValidationReportScrollBarVisible()
    {
        if (_validationReportTextBox.IsHandleCreated)
        {
            ShowScrollBar(_validationReportTextBox.Handle, VerticalScrollBar, true);
        }
    }

    private void HelpButton_Click(object? sender, EventArgs e)
    {
        var helpPath = Path.Combine(AppContext.BaseDirectory, "epanetjdahelp.pdf");
        if (!File.Exists(helpPath))
        {
            MessageBox.Show(this, "The help PDF could not be found.", "Help", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(helpPath)
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"Unable to open the help PDF.\r\n{exception.Message}", "Help", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static Button CreateTitleBarButton(string text, int x) => new()
    {
        BackColor = SystemColors.Control,
        FlatStyle = FlatStyle.Flat,
        FlatAppearance =
        {
            BorderSize = 0,
            MouseDownBackColor = SystemColors.Control,
            MouseOverBackColor = SystemColors.Control
        },
        Location = new Point(x, 0),
        Size = new Size(TitleBarButtonWidth, TitleBarHeight),
        TabStop = false,
        Text = text,
        UseVisualStyleBackColor = false
    };

    private static void MinimizeWindowButton_Paint(object? sender, PaintEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        const int dashWidth = 12;
        const int dashHeight = 1;
        var x = (button.ClientSize.Width - dashWidth) / 2;
        var y = (button.ClientSize.Height - dashHeight) / 2;
        using var brush = new SolidBrush(button.ForeColor);
        e.Graphics.FillRectangle(brush, x, y, dashWidth, dashHeight);
    }

    private void TitleBar_MouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            ReleaseCapture();
            SendMessage(Handle, 0x00A1, (IntPtr)2, IntPtr.Zero);
        }
    }

    private static DataGridViewTextBoxColumn CreateZoneColumn(string name, string heading, float fillWeight) => new()
    {
        FillWeight = fillWeight,
        MinimumWidth = 60,
        Resizable = DataGridViewTriState.True,
        HeaderText = heading,
        Name = name,
        SortMode = DataGridViewColumnSortMode.NotSortable
    };

    private void MethodComboBox_SelectedIndexChanged(object? sender, EventArgs e)
    {
        MarkUnsavedChanges();
        UpdateZoneModifierColumnHeaders();
        ClearValidationReport();
    }

    private void UpdateZoneModifierColumnHeaders()
    {
        if (_zoneGrid.Columns.Count == 0)
        {
            return;
        }

        var isUnitLength = GetSelectedDistributionMethod() == DemandDistributionMethod.UnitLength;
        if (_zoneGrid.Columns["KHalfPipes"] is { } halfModifierColumn)
        {
            halfModifierColumn.HeaderText = isUnitLength ? "K = 0.5 Pipes" : "50% Junctions";
        }

        if (_zoneGrid.Columns["KZeroPipes"] is { } zeroModifierColumn)
        {
            zeroModifierColumn.HeaderText = isUnitLength ? "K = 0 Pipes" : "0% Junctions";
        }
    }

    private void ZoneCountSpinner_ValueChanged(object? sender, EventArgs e)
    {
        MarkUnsavedChanges();
        SyncZoneRows((int)_zoneCountSpinner.Value);
        ClearValidationReport();
        RefreshInputStatus();
    }

    private void InputFields_Changed(object? sender, EventArgs e)
    {
        MarkUnsavedChanges();
        ClearValidationReport();
        RefreshInputStatus();
    }

    private void ZoneGrid_CellEndEdit(object? sender, DataGridViewCellEventArgs e)
    {
        MarkUnsavedChanges();
        ClearValidationReport();
        RefreshInputStatus();
    }

    private void SyncZoneRows(int zoneCount)
    {
        // Keep grid rows synchronized with the spinner and assign stable default zone names.
        while (_zoneGrid.Rows.Count > zoneCount)
        {
            _zoneGrid.Rows.RemoveAt(_zoneGrid.Rows.Count - 1);
        }

        while (_zoneGrid.Rows.Count < zoneCount)
        {
            var rowIndex = _zoneGrid.Rows.Add();
            _zoneGrid.Rows[rowIndex].Cells["Zone"].Value = $"Z{rowIndex + 1}";
        }
    }

    private void BrowseButton_Click(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            CheckFileExists = true,
            Filter = "EPANET input files (*.inp)|*.inp|All files (*.*)|*.*",
            Multiselect = false,
            Title = "Select EPANET input file"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _inputPathTextBox.Text = dialog.FileName;
        ClearValidationReport();
        SetValidationMessage(string.Empty, SystemColors.ControlText);

        try
        {
            // Read the network once and retain the summary used for status, validation, and calculations.
            _network = EpanetNetworkReader.Read(dialog.FileName);
            MarkUnsavedChanges();
            var unitIndex = _unitComboBox.Items.IndexOf(_network.FlowUnits);
            if (unitIndex >= 0)
            {
                _unitComboBox.SelectedIndex = unitIndex;
            }

            UpdateNetworkSummary();
            RefreshInputStatus();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _network = null;
            _statusLabel.Text = "Could not read EPANET network";
            SetValidationMessage("Could not read the selected EPANET network.", Color.Firebrick);
            MessageBox.Show(this, exception.Message, "Input file error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SaveSessionButton_Click(object? sender, EventArgs e)
    {
        TrySaveSession();
    }

    private bool TrySaveSession()
    {
        if (_network is null || string.IsNullOrWhiteSpace(_inputPathTextBox.Text))
        {
            MessageBox.Show(this, "Select a valid EPANET input file before saving a session.", "Save session", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        using var dialog = new SaveFileDialog
        {
            AddExtension = true,
            DefaultExt = "dgsession.json",
            FileName = $"{Path.GetFileNameWithoutExtension(_inputPathTextBox.Text)}.dgsession.json",
            Filter = "Demand Allocator session (*.dgsession.json)|*.dgsession.json|JSON files (*.json)|*.json",
            OverwritePrompt = true,
            Title = "Save demand generator session"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return false;
        }

        // Commit any active cell edit before copying values into the session record.
        _zoneGrid.EndEdit();
        var zones = _zoneGrid.Rows
            .Cast<DataGridViewRow>()
            .Select(row => new ZoneInstructionDraft(
                row.Cells["Zone"].Value?.ToString(),
                row.Cells["Demand"].Value?.ToString(),
                row.Cells["Junctions"].Value?.ToString(),
                row.Cells["KHalfPipes"].Value?.ToString(),
                row.Cells["KZeroPipes"].Value?.ToString()))
            .ToArray();

        try
        {
            DemandSessionStore.Save(
                dialog.FileName,
                new DemandSession(
                    _inputPathTextBox.Text,
                    _methodComboBox.SelectedItem?.ToString(),
                    _totalDemandTextBox.Text,
                    _unitComboBox.SelectedItem?.ToString(),
                    (int)_zoneCountSpinner.Value,
                    zones));
            _hasUnsavedChanges = false;
            SetValidationMessage("Session saved.", Color.DarkGreen);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            SetValidationMessage("Could not save the session.", Color.Firebrick);
            MessageBox.Show(this, exception.Message, "Save session error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private void Form1_FormClosing(object? sender, FormClosingEventArgs e)
    {
        _zoneGrid.EndEdit();
        if (_allowClose || !_hasUnsavedChanges)
        {
            return;
        }

        // The session is optional, so let the user save, discard, or cancel the close operation.
        var response = MessageBox.Show(
            this,
            "Save your changes before exiting?\r\n\r\nYes: Save and exit\r\nNo: Exit without saving\r\nCancel: Return to the program",
            "Unsaved changes",
            MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button1);

        if (response == DialogResult.No)
        {
            _allowClose = true;
            return;
        }

        if (response == DialogResult.Yes && TrySaveSession())
        {
            _allowClose = true;
            return;
        }

        e.Cancel = true;
    }

    private void MarkUnsavedChanges() => _hasUnsavedChanges = true;

    private void LoadSessionButton_Click(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            CheckFileExists = true,
            Filter = "Demand Generator session (*.dgsession.json;*.json)|*.dgsession.json;*.json|All files (*.*)|*.*",
            Multiselect = false,
            Title = "Load demand generator session"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            // Validate saved paths and selections before changing the current form state.
            var session = DemandSessionStore.Load(dialog.FileName);
            if (!File.Exists(session.NetworkPath))
            {
                throw new FileNotFoundException("The input network saved in this session could not be found.", session.NetworkPath);
            }

            var network = EpanetNetworkReader.Read(session.NetworkPath);
            if (session.Method is null || !_methodComboBox.Items.Contains(session.Method))
            {
                throw new InvalidDataException("The saved distribution method is not supported.");
            }

            if (session.FlowUnits is not null && !_unitComboBox.Items.Contains(session.FlowUnits))
            {
                throw new InvalidDataException("The saved flow unit is not supported.");
            }

            _network = network;
            _inputPathTextBox.Text = session.NetworkPath;
            _totalDemandTextBox.Text = session.TotalDemand ?? string.Empty;
            _methodComboBox.SelectedItem = session.Method;
            _unitComboBox.SelectedItem = session.FlowUnits;
            _zoneCountSpinner.Value = session.ZoneCount;
            for (var index = 0; index < session.Zones.Count; index++)
            {
                var zone = session.Zones[index];
                var row = _zoneGrid.Rows[index];
                row.Cells["Zone"].Value = zone.Name;
                row.Cells["Demand"].Value = zone.Demand;
                row.Cells["Junctions"].Value = zone.Junctions;
                row.Cells["KHalfPipes"].Value = zone.KHalfPipes;
                row.Cells["KZeroPipes"].Value = zone.KZeroPipes;
            }

            ClearValidationReport();
            UpdateNetworkSummary();
            RefreshInputStatus();
            _hasUnsavedChanges = false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            SetValidationMessage("Could not load the saved session.", Color.Firebrick);
            MessageBox.Show(this, exception.Message, "Load session error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void UpdateNetworkSummary()
    {
        if (_network is null)
        {
            _statusLabel.Text = "No input file selected";
            return;
        }

        var unitText = _network.FlowUnits is null ? "unknown units" : _network.FlowUnits;
        _statusLabel.Text = $"{_network.JunctionIds.Count} junctions | {_network.ReservoirIds.Count} reservoirs | " +
            $"{_network.TankIds.Count} tanks | {_network.Pipes.Count} pipes | {_network.PumpCount} pumps | " +
            $"{_network.ValveCount} valves | {unitText}";
    }

    private void ValidateButton_Click(object? sender, EventArgs e)
    {
        if (_network is null)
        {
            _validationResult = null;
            _calculateButton.Enabled = false;
            _validationReportTextBox.Text = "Validation Error. See detail validation report.\r\nValidation errors\r\n- Select a valid EPANET input file first.";
            SetValidationMessage("Validation Error. See detail validation report.", Color.Firebrick);
            return;
        }

        // Commit the active grid cell so validation sees the latest value the user entered.
        _zoneGrid.EndEdit();
        var zoneDrafts = _zoneGrid.Rows
            .Cast<DataGridViewRow>()
            .Select(row => new ZoneInstructionDraft(
                row.Cells["Zone"].Value?.ToString(),
                row.Cells["Demand"].Value?.ToString(),
                row.Cells["Junctions"].Value?.ToString(),
                row.Cells["KHalfPipes"].Value?.ToString(),
                row.Cells["KZeroPipes"].Value?.ToString()))
            .ToArray();

        var result = DemandInputValidator.Validate(
            _network,
            GetSelectedDistributionMethod(),
            _totalDemandTextBox.Text,
            _unitComboBox.SelectedItem?.ToString(),
            zoneDrafts);
        _validationResult = result;
        _calculateButton.Enabled = result.IsValid;
        _validationReportTextBox.Text = result.BuildReport();
        if (!result.IsValid)
        {
            SetValidationMessage("Validation Error. See detail validation report.", Color.Firebrick);
        }
        else if (result.Warnings.Count > 0)
        {
            SetValidationMessage("Validation passed with a demand-rounding warning; review details above.", Color.DarkGoldenrod);
        }
        else
        {
            SetValidationMessage("Validation passed. No demands have been calculated or written.", Color.DarkGreen);
        }
    }

    private DemandDistributionMethod GetSelectedDistributionMethod() =>
        _methodComboBox.SelectedItem?.ToString() switch
        {
            "Unit-Length" => DemandDistributionMethod.UnitLength,
            "Equal-Distribution" => DemandDistributionMethod.EqualDistribution,
            _ => throw new InvalidOperationException("Select a supported distribution method.")
        };

    private void ClearValidationReport()
    {
        _validationReportTextBox.Clear();
        _validationResult = null;
        _calculateButton.Enabled = false;
    }

    private void CalculateButton_Click(object? sender, EventArgs e)
    {
        if (_network is null || _validationResult is null || !_validationResult.IsValid)
        {
            return;
        }

        var currentStage = "calculating demands";
        _calculateButton.Enabled = false;
        UseWaitCursor = true;
        _statusLabel.Text = "Calculating demands...";
        _statusLabel.Refresh();
        try
        {
            // Delegate each method to its own distributor; the UI only orchestrates the workflow.
            var distribution = _validationResult.Method switch
            {
                DemandDistributionMethod.UnitLength => UnitLengthDemandDistributor.Calculate(_network, _validationResult),
                DemandDistributionMethod.EqualDistribution => EqualDistributionDemandDistributor.Calculate(_validationResult),
                _ => throw new InvalidOperationException("The selected distribution method is not supported.")
            };

            currentStage = "writing and verifying the generated .inp file";
            _statusLabel.Text = "Writing and verifying generated .inp file...";
            _statusLabel.Refresh();
            var generatedFilePath = EpanetDemandFileWriter.WriteVerifiedCopy(_inputPathTextBox.Text, distribution);

            currentStage = "writing the detailed .txt report";
            _statusLabel.Text = "Writing detailed .txt report...";
            _statusLabel.Refresh();
            var reportFilePath = DemandGenerationReportWriter.Write(
                _inputPathTextBox.Text,
                generatedFilePath,
                _network,
                _validationResult,
                distribution);
            _validationReportTextBox.Text = distribution.BuildReport(_validationResult.FlowUnits);
            _statusLabel.Text = "Generated .inp and detailed .txt report.";
            SetValidationMessage(
                $"Generated files: {Path.GetFileName(generatedFilePath)} and {Path.GetFileName(reportFilePath)}",
                Color.DarkGreen);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            _validationReportTextBox.Text = $"Generation stopped while {currentStage}.\r\n{exception.Message}";
            _statusLabel.Text = $"Generation stopped while {currentStage}.";
            SetValidationMessage($"Could not complete while {currentStage}.", Color.Firebrick);
        }
        finally
        {
            UseWaitCursor = false;
            _calculateButton.Enabled = _validationResult?.IsValid == true;
        }
    }

    private void RefreshInputStatus()
    {
        SetValidationMessage(string.Empty, SystemColors.ControlText);
        if (_network is null)
        {
            return;
        }

        // Provide immediate feedback for unit mismatches and demand totals before full validation.
        var selectedUnits = _unitComboBox.SelectedItem?.ToString();
        if (selectedUnits is not null
            && _network.FlowUnits is not null
            && !selectedUnits.Equals(_network.FlowUnits, StringComparison.OrdinalIgnoreCase))
        {
            SetValidationMessage(
                $"Selected flow unit '{selectedUnits}' does not match network unit '{_network.FlowUnits}'.",
                Color.Firebrick);
            return;
        }

        if (!TryParseDemand(_totalDemandTextBox.Text, out var totalDemand))
        {
            return;
        }

        var zoneDemands = new List<double>(_zoneGrid.Rows.Count);
        foreach (DataGridViewRow row in _zoneGrid.Rows)
        {
            if (!TryParseDemand(row.Cells["Demand"].Value?.ToString(), out var zoneDemand))
            {
                return;
            }

            zoneDemands.Add(zoneDemand);
        }

        var difference = Math.Abs(zoneDemands.Sum() - totalDemand);
        var tolerance = Math.Abs(totalDemand) * DemandInputValidator.DemandMismatchToleranceFraction;
        var roundingTolerance = Math.Max(1.0, Math.Abs(totalDemand)) * 1e-9;
        if (difference <= roundingTolerance)
        {
            return;
        }

        var differenceText = totalDemand == 0
            ? "an undefined percentage of the zero total"
            : $"{difference / Math.Abs(totalDemand) * 100:F3}%";
        if (difference > tolerance)
        {
            SetValidationMessage(
                $"Zone demands differ from the total by {differenceText}; correct the discrepancy.",
                Color.Firebrick);
        }
        else
        {
            SetValidationMessage(
                $"Zone totals differ from the Network total by {difference.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)} {selectedUnits ?? "unknown"}.",
                Color.DarkGoldenrod);
        }
    }

    private static bool TryParseDemand(string? text, out double value)
    {
        return (double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out value)
                || double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value))
            && double.IsFinite(value)
            && value >= 0;
    }

    private void SetValidationMessage(string message, Color color)
    {
        _validationMessageLabel.Text = message;
        _validationMessageLabel.ForeColor = color;
    }

    private void AddHoverHelp(Control control, string message)
    {
        control.MouseEnter += (_, _) => _hoverHelpLabel.Text = message;
        control.MouseLeave += (_, _) => _hoverHelpLabel.Text = string.Empty;
    }

    private void CopyrightButton_Click(object? sender, EventArgs e)
    {
        MessageBox.Show(
            this,
            $"EPANET-JDA\nCopyright \u00a9 {DateTime.Now.Year} Soumitra Ganguly",
            "Copyright",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }
}
