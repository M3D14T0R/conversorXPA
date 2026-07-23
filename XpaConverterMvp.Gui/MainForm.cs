using System.Text;
using XpaConverterMvp;

namespace XpaConverterMvp.Gui;

internal sealed class MainForm : Form
{
    private const string UserPreferencesFileName = "user-preferences.json";
#if DEBUG
    private const string BuildConfigurationLabel = "Debug";
#else
    private const string BuildConfigurationLabel = "Release";
#endif
    private readonly TextBox _xmlPath = new() { Dock = DockStyle.Fill };
    private readonly TextBox _outputDir = new() { Dock = DockStyle.Fill };
    private readonly TextBox _namespace = new() { Dock = DockStyle.Fill };
    private readonly ComboBox _outputType = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _runtimeCoreReferenceMode = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _runtimeCoreDllPath = new() { Dock = DockStyle.Fill };
    private readonly TextBox _runtimeRootPath = new() { Dock = DockStyle.Fill };
    private readonly TextBox _componentXmls = new() { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical };
    private readonly TextBox _tablesXmls = new() { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical };
    private readonly DataGridView _projectRefMap = new()
    {
        Dock = DockStyle.Fill,
        AutoGenerateColumns = false,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect
    };
    private readonly RadioButton _modeComplete = new() { Text = "Completa", Checked = true, AutoSize = true };
    private readonly RadioButton _modeFolder = new() { Text = "Por pasta", AutoSize = true };
    private readonly RadioButton _modeTask = new() { Text = "Por task", AutoSize = true };
    private readonly RadioButton _modeTaskRange = new() { Text = "Por range", AutoSize = true };
    private readonly ComboBox _folder = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _taskFilter = new() { Dock = DockStyle.Fill };
    private readonly TextBox _taskRanges = new() { Dock = DockStyle.Fill, PlaceholderText = "1-100; 101-200" };
    private readonly CheckedListBox _tasks = new() { Dock = DockStyle.Fill, CheckOnClick = true };
    private readonly CheckBox _withDependencies = new() { Text = "Incluir dependências", AutoSize = true };
    private readonly CheckBox _incrementalOutput = new() { Text = "Incrementar projeto existente (não limpar output)", AutoSize = true };
    private readonly CheckBox _internalCompat = new() { Text = "Atualizar compat interno para tasks não convertidas", AutoSize = true, Checked = true, Enabled = false };
    private readonly CheckBox _fullSolution = new() { Text = "Gerar solução completa", AutoSize = true };
    private readonly CheckBox _parallelTaskGeneration = new() { Text = "Paralelizar tasks (experimental)", AutoSize = true };
    private readonly CheckBox _dynamicWorkers = new() { Text = "Workers dinâmicos", AutoSize = true, Checked = true };
    private readonly NumericUpDown _parallelMaxWorkers = new() { Minimum = 1, Maximum = 64, Value = 8, Width = 80 };
    private readonly NumericUpDown _parallelMinWorkers = new() { Minimum = 1, Maximum = 64, Value = 2, Width = 80 };
    private readonly NumericUpDown _parallelInitialWorkers = new() { Minimum = 1, Maximum = 64, Value = 4, Width = 80 };
    private readonly NumericUpDown _parallelInitialUntilPercent = new() { Minimum = 0, Maximum = 100, Value = 10, Width = 80 };
    private readonly TextBox _parallelMaxMemory = new() { Text = "90%", Width = 80 };
    private readonly NumericUpDown _cacheResetEveryTasks = new() { Minimum = 0, Maximum = 1000, Value = 60, Width = 80 };
    private readonly TextBox _commandPreview = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    private readonly TextBox _log = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    private readonly Button _run = new() { Text = "Executar", AutoSize = true };
    private readonly Label _catalogStatus = new() { AutoSize = true };
    private readonly ComboBox _referenceFilter = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };

    private List<string> _allTasks = new();
    private List<string> _allComponents = new();
    private List<XmlCatalogReference> _allReferences = new();
    private bool _isLoadingCatalog;

    private sealed record UserPreferences(
        string? RuntimeCoreReferenceMode,
        string? RuntimeCoreDllPath,
        string? RuntimeRootPath,
        bool? FullSolution,
        bool? ParallelTaskGeneration,
        bool? IncrementalOutput,
        bool? DynamicWorkers,
        int? ParallelMaxWorkers,
        int? ParallelMinWorkers,
        int? ParallelInitialWorkers,
        int? ParallelInitialUntilPercent,
        string? ParallelMaxMemory,
        int? CacheResetEveryTasks);

    public MainForm()
    {
        _outputType.Items.AddRange(new object[] { "WinExe", "ClassLibrary" });
        _outputType.SelectedIndex = 0;
        _runtimeCoreReferenceMode.Items.AddRange(new object[] { "Project", "Dll" });
        _runtimeCoreReferenceMode.SelectedIndex = 0;
        _referenceFilter.Items.AddRange(new object[] { "Todas", "Somente XPA", "Somente .NET/DLL", "Somente não mapeadas" });
        _referenceFilter.SelectedIndex = 0;
        InitializeProjectRefGrid();
        Text = $"XpaConverterMvp Launcher [{BuildConfigurationLabel}]";
        Width = 1180;
        Height = 820;
        StartPosition = FormStartPosition.CenterScreen;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(10)
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 18));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        Controls.Add(root);

        root.Controls.Add(BuildPathPanel("XML", _xmlPath, BrowseXml), 0, 0);
        root.Controls.Add(BuildPathPanel("Output", _outputDir, BrowseOutput), 0, 1);
        root.Controls.Add(BuildMainTabs(), 0, 2);

        var previewGroup = new GroupBox { Text = "Preview do comando", Dock = DockStyle.Fill };
        previewGroup.Controls.Add(_commandPreview);
        root.Controls.Add(previewGroup, 0, 3);

        var logGroup = new GroupBox { Text = "Log", Dock = DockStyle.Fill };
        logGroup.Controls.Add(_log);
        root.Controls.Add(logGroup, 0, 4);

        HookEvents();
        LoadUserPreferences();
        RefreshUiState();
        RefreshCommandPreview();
    }

    private static string GetUserPreferencesPath()
        => Path.Combine(Application.UserAppDataPath, UserPreferencesFileName);

    private void LoadUserPreferences()
    {
        try
        {
            var path = GetUserPreferencesPath();
            if (!File.Exists(path))
                return;

            var json = File.ReadAllText(path);
            var preferences = System.Text.Json.JsonSerializer.Deserialize<UserPreferences>(json);
            if (preferences is null)
                return;

            if (!string.IsNullOrWhiteSpace(preferences.RuntimeCoreReferenceMode))
            {
                var selected = _runtimeCoreReferenceMode.Items
                    .Cast<object>()
                    .Select(item => item?.ToString())
                    .FirstOrDefault(item => string.Equals(item, preferences.RuntimeCoreReferenceMode, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(selected))
                    _runtimeCoreReferenceMode.SelectedItem = selected;
            }

            _runtimeCoreDllPath.Text = preferences.RuntimeCoreDllPath ?? "";
            _runtimeRootPath.Text = preferences.RuntimeRootPath ?? "";
            if (preferences.FullSolution.HasValue)
                _fullSolution.Checked = preferences.FullSolution.Value;
            if (preferences.ParallelTaskGeneration.HasValue)
                _parallelTaskGeneration.Checked = preferences.ParallelTaskGeneration.Value;
            if (preferences.IncrementalOutput.HasValue)
                _incrementalOutput.Checked = preferences.IncrementalOutput.Value;
            if (preferences.DynamicWorkers.HasValue)
                _dynamicWorkers.Checked = preferences.DynamicWorkers.Value;
            if (preferences.ParallelMaxWorkers.HasValue)
                _parallelMaxWorkers.Value = Math.Clamp(preferences.ParallelMaxWorkers.Value, (int)_parallelMaxWorkers.Minimum, (int)_parallelMaxWorkers.Maximum);
            if (preferences.ParallelMinWorkers.HasValue)
                _parallelMinWorkers.Value = Math.Clamp(preferences.ParallelMinWorkers.Value, (int)_parallelMinWorkers.Minimum, (int)_parallelMinWorkers.Maximum);
            if (preferences.ParallelInitialWorkers.HasValue)
                _parallelInitialWorkers.Value = Math.Clamp(preferences.ParallelInitialWorkers.Value, (int)_parallelInitialWorkers.Minimum, (int)_parallelInitialWorkers.Maximum);
            if (preferences.ParallelInitialUntilPercent.HasValue)
                _parallelInitialUntilPercent.Value = Math.Clamp(preferences.ParallelInitialUntilPercent.Value, (int)_parallelInitialUntilPercent.Minimum, (int)_parallelInitialUntilPercent.Maximum);
            if (!string.IsNullOrWhiteSpace(preferences.ParallelMaxMemory))
                _parallelMaxMemory.Text = preferences.ParallelMaxMemory.Trim();
            if (preferences.CacheResetEveryTasks.HasValue)
                _cacheResetEveryTasks.Value = Math.Clamp(preferences.CacheResetEveryTasks.Value, (int)_cacheResetEveryTasks.Minimum, (int)_cacheResetEveryTasks.Maximum);
        }
        catch (Exception ex)
        {
            AppendLogLine($"Aviso ao carregar preferências do usuário: {ex.Message}");
        }
    }

    private void SaveUserPreferences()
    {
        try
        {
            var preferences = new UserPreferences(
                _runtimeCoreReferenceMode.SelectedItem?.ToString(),
                string.IsNullOrWhiteSpace(_runtimeCoreDllPath.Text) ? null : _runtimeCoreDllPath.Text.Trim(),
                string.IsNullOrWhiteSpace(_runtimeRootPath.Text) ? null : _runtimeRootPath.Text.Trim(),
                _fullSolution.Checked,
                _parallelTaskGeneration.Checked,
                _incrementalOutput.Checked,
                _dynamicWorkers.Checked,
                (int)_parallelMaxWorkers.Value,
                (int)_parallelMinWorkers.Value,
                (int)_parallelInitialWorkers.Value,
                (int)_parallelInitialUntilPercent.Value,
                _parallelMaxMemory.Text.Trim(),
                (int)_cacheResetEveryTasks.Value);

            var path = GetUserPreferencesPath();
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var json = System.Text.Json.JsonSerializer.Serialize(preferences, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            AppendLogLine($"Aviso ao salvar preferências do usuário: {ex.Message}");
        }
    }

    private Control BuildPathPanel(string label, TextBox box, EventHandler browseHandler)
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 3, AutoSize = true };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));

        panel.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        panel.Controls.Add(box, 1, 0);
        var browse = new Button { Text = "Procurar", AutoSize = true };
        browse.Click += browseHandler;
        panel.Controls.Add(browse, 2, 0);
        return panel;
    }

    private Control BuildMainTabs()
    {
        var tabs = new TabControl { Dock = DockStyle.Fill };

        var generalTab = new TabPage("Geral");
        generalTab.Controls.Add(BuildGeneralPanel());
        tabs.TabPages.Add(generalTab);

        var supportTab = new TabPage("Apoio");
        supportTab.Controls.Add(BuildSupportFilesPanel());
        tabs.TabPages.Add(supportTab);

        var refsTab = new TabPage("Referências");
        refsTab.Controls.Add(BuildReferencesPanel());
        tabs.TabPages.Add(refsTab);

        var scopeTab = new TabPage("Escopo");
        scopeTab.Controls.Add(BuildFiltersPanel());
        tabs.TabPages.Add(scopeTab);

        var performanceTab = new TabPage("Performance");
        performanceTab.Controls.Add(BuildPerformancePanel());
        tabs.TabPages.Add(performanceTab);

        return tabs;
    }

    private Control BuildGeneralPanel()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(8), AutoScroll = true };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var general = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        general.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        general.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        general.Controls.Add(new Label { Text = "Namespace", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        general.Controls.Add(_namespace, 1, 0);

        general.Controls.Add(new Label { Text = "Tipo saída", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        general.Controls.Add(_outputType, 1, 1);

        general.Controls.Add(new Label { Text = "XPARuntimeCore", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        general.Controls.Add(_runtimeCoreReferenceMode, 1, 2);

        var runtimeCoreDllPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
        runtimeCoreDllPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        runtimeCoreDllPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var browseRuntimeCoreDll = new Button { Text = "Procurar", AutoSize = true };
        browseRuntimeCoreDll.Click += (_, _) => BrowseRuntimeCoreDll();
        runtimeCoreDllPanel.Controls.Add(_runtimeCoreDllPath, 0, 0);
        runtimeCoreDllPanel.Controls.Add(browseRuntimeCoreDll, 1, 0);
        general.Controls.Add(new Label { Text = "XPARuntimeCore DLL", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 3);
        general.Controls.Add(runtimeCoreDllPanel, 1, 3);

        var runtimePanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
        runtimePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        runtimePanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var browseRuntime = new Button { Text = "Procurar", AutoSize = true };
        browseRuntime.Click += (_, _) => BrowseRuntimeRoot();
        runtimePanel.Controls.Add(_runtimeRootPath, 0, 0);
        runtimePanel.Controls.Add(browseRuntime, 1, 0);
        general.Controls.Add(new Label { Text = "Runtime", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 4);
        general.Controls.Add(runtimePanel, 1, 4);

        var modeFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        modeFlow.Controls.Add(_modeComplete);
        modeFlow.Controls.Add(_modeFolder);
        modeFlow.Controls.Add(_modeTask);
        modeFlow.Controls.Add(_modeTaskRange);
        general.Controls.Add(new Label { Text = "Modo", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 5);
        general.Controls.Add(modeFlow, 1, 5);

        var flags = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        flags.Controls.Add(_withDependencies);
        flags.Controls.Add(_fullSolution);
        flags.Controls.Add(_parallelTaskGeneration);
        general.Controls.Add(new Label { Text = "Flags", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 6);
        general.Controls.Add(flags, 1, 6);

        _run.Click += async (_, _) => await RunConversionAsync();
        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
        footer.Controls.Add(_run);

        panel.Controls.Add(general, 0, 0);
        panel.Controls.Add(footer, 0, 1);
        return panel;
    }

    private Control BuildPerformancePanel()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, Padding = new Padding(8), AutoSize = true };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        panel.Controls.Add(new Label { Text = "Workers", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        panel.Controls.Add(_dynamicWorkers, 1, 0);
        panel.Controls.Add(new Label { Text = "Máximo", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        panel.Controls.Add(_parallelMaxWorkers, 1, 1);
        panel.Controls.Add(new Label { Text = "Mínimo", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        panel.Controls.Add(_parallelMinWorkers, 1, 2);
        panel.Controls.Add(new Label { Text = "Início", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 3);
        panel.Controls.Add(_parallelInitialWorkers, 1, 3);
        panel.Controls.Add(new Label { Text = "Início até (%)", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 4);
        panel.Controls.Add(_parallelInitialUntilPercent, 1, 4);
        panel.Controls.Add(new Label { Text = "RAM máxima", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 5);
        panel.Controls.Add(_parallelMaxMemory, 1, 5);
        panel.Controls.Add(new Label { Text = "Reset cache a cada", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 6);
        panel.Controls.Add(_cacheResetEveryTasks, 1, 6);

        return panel;
    }
    private Control BuildSupportFilesPanel()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, Padding = new Padding(8) };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

        var componentButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var addComponentXml = new Button { Text = "Adicionar", AutoSize = true };
        var clearComponentXml = new Button { Text = "Limpar", AutoSize = true };
        addComponentXml.Click += (_, _) => BrowseMultiXmlInto(_componentXmls);
        clearComponentXml.Click += (_, _) => _componentXmls.Clear();
        componentButtons.Controls.Add(addComponentXml);
        componentButtons.Controls.Add(clearComponentXml);
        panel.Controls.Add(new Label { Text = "XMLs comp.", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        panel.Controls.Add(_componentXmls, 1, 0);
        panel.Controls.Add(componentButtons, 2, 0);

        var tablesButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var addTablesXml = new Button { Text = "Adicionar", AutoSize = true };
        var clearTablesXml = new Button { Text = "Limpar", AutoSize = true };
        addTablesXml.Click += (_, _) => BrowseMultiXmlInto(_tablesXmls);
        clearTablesXml.Click += (_, _) => _tablesXmls.Clear();
        tablesButtons.Controls.Add(addTablesXml);
        tablesButtons.Controls.Add(clearTablesXml);
        panel.Controls.Add(new Label { Text = "XMLs tabelas", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        panel.Controls.Add(_tablesXmls, 1, 1);
        panel.Controls.Add(tablesButtons, 2, 1);

        return panel;
    }

    private Control BuildReferencesPanel()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, Padding = new Padding(8) };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var filterPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
        filterPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        filterPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        filterPanel.Controls.Add(new Label { Text = "Filtro", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        filterPanel.Controls.Add(_referenceFilter, 1, 0);

        var projectRefButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var mapProjectRef = new Button { Text = "Mapear", AutoSize = true };
        var autoMapProjectRef = new Button { Text = "Auto map", AutoSize = true };
        var clearProjectRef = new Button { Text = "Limpar", AutoSize = true };
        mapProjectRef.Click += (_, _) => BrowseProjectForSelectedComponent();
        autoMapProjectRef.Click += async (_, _) => await AutoMapVisibleReferencesAsync(autoMapProjectRef);
        clearProjectRef.Click += (_, _) => ClearSelectedProjectMapping();
        projectRefButtons.Controls.Add(mapProjectRef);
        projectRefButtons.Controls.Add(autoMapProjectRef);
        projectRefButtons.Controls.Add(clearProjectRef);

        panel.Controls.Add(filterPanel, 1, 0);
        panel.Controls.Add(new Label { Text = "Refs XPA/.NET", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        panel.Controls.Add(_projectRefMap, 1, 1);
        panel.Controls.Add(projectRefButtons, 2, 1);

        return panel;
    }

    private Control BuildFiltersPanel()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(8) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var statusPanel = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
        _catalogStatus.Text = "Catálogo não carregado.";
        statusPanel.Controls.Add(_catalogStatus);
        layout.Controls.Add(statusPanel, 0, 0);

        var folderPanel = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true };
        folderPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        folderPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        folderPanel.Controls.Add(new Label { Text = "Pasta XPA", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        folderPanel.Controls.Add(_folder, 1, 0);
        layout.Controls.Add(folderPanel, 0, 1);

        var taskFilterPanel = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true };
        taskFilterPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        taskFilterPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        taskFilterPanel.Controls.Add(new Label { Text = "Filtro task", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        taskFilterPanel.Controls.Add(_taskFilter, 1, 0);
        layout.Controls.Add(taskFilterPanel, 0, 2);

        var taskRangePanel = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true };
        taskRangePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        taskRangePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        taskRangePanel.Controls.Add(new Label { Text = "Range task", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        taskRangePanel.Controls.Add(_taskRanges, 1, 0);
        layout.Controls.Add(taskRangePanel, 0, 3);

        var incrementalPanel = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
        incrementalPanel.Controls.Add(_incrementalOutput);
        incrementalPanel.Controls.Add(_internalCompat);
        layout.Controls.Add(incrementalPanel, 0, 4);

        layout.Controls.Add(_tasks, 0, 5);

        return layout;
    }

    private void HookEvents()
    {
        _xmlPath.TextChanged += async (_, _) =>
        {
            RefreshNamespaceFromInputs();
            await LoadCatalogFromXmlAsync();
            RefreshCommandPreview();
        };
        _outputDir.TextChanged += (_, _) =>
        {
            RefreshNamespaceFromInputs();
            RefreshCommandPreview();
        };
        _namespace.TextChanged += (_, _) => RefreshCommandPreview();
        _outputType.SelectedIndexChanged += (_, _) => RefreshCommandPreview();
        _runtimeCoreReferenceMode.SelectedIndexChanged += (_, _) => RefreshUiState();
        _runtimeCoreDllPath.TextChanged += (_, _) => RefreshCommandPreview();
        _runtimeRootPath.TextChanged += (_, _) => RefreshCommandPreview();
        _componentXmls.TextChanged += (_, _) => RefreshCommandPreview();
        _tablesXmls.TextChanged += (_, _) => RefreshCommandPreview();
        _projectRefMap.CellValueChanged += (_, _) => RefreshCommandPreview();
        _projectRefMap.CellFormatting += ProjectRefMapOnCellFormatting;
        _projectRefMap.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_projectRefMap.IsCurrentCellDirty)
                _projectRefMap.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _referenceFilter.SelectedIndexChanged += (_, _) => PopulateProjectReferenceGrid();
        _folder.SelectedIndexChanged += (_, _) => RefreshCommandPreview();
        _taskFilter.TextChanged += (_, _) => ApplyTaskFilter();
        _taskRanges.TextChanged += (_, _) => RefreshCommandPreview();
        _tasks.ItemCheck += (_, _) => BeginInvoke(new Action(RefreshCommandPreview));
        _withDependencies.CheckedChanged += (_, _) => RefreshUiState();
        _incrementalOutput.CheckedChanged += (_, _) => RefreshCommandPreview();
        _fullSolution.CheckedChanged += (_, _) => RefreshCommandPreview();
        _parallelTaskGeneration.CheckedChanged += (_, _) => RefreshUiState();
        _dynamicWorkers.CheckedChanged += (_, _) => RefreshCommandPreview();
        _parallelMaxWorkers.ValueChanged += (_, _) => RefreshCommandPreview();
        _parallelMinWorkers.ValueChanged += (_, _) => RefreshCommandPreview();
        _parallelInitialWorkers.ValueChanged += (_, _) => RefreshCommandPreview();
        _parallelInitialUntilPercent.ValueChanged += (_, _) => RefreshCommandPreview();
        _parallelMaxMemory.TextChanged += (_, _) => RefreshCommandPreview();
        _cacheResetEveryTasks.ValueChanged += (_, _) => RefreshCommandPreview();
        _modeComplete.CheckedChanged += (_, _) => RefreshUiState();
        _modeFolder.CheckedChanged += (_, _) => RefreshUiState();
        _modeTask.CheckedChanged += (_, _) => RefreshUiState();
        _modeTaskRange.CheckedChanged += (_, _) => RefreshUiState();
    }

    private void RefreshNamespaceFromInputs()
    {
        var xmlPath = _xmlPath.Text.Trim();
        if (!string.IsNullOrWhiteSpace(xmlPath))
        {
            var fileName = Path.GetFileNameWithoutExtension(xmlPath);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                _namespace.Text = fileName;
                return;
            }
        }

        var outputDir = _outputDir.Text.Trim();
        if (!string.IsNullOrWhiteSpace(outputDir))
            _namespace.Text = ConversionRunner.InferNamespaceFromOutputDirectory(outputDir);
    }

    private async Task LoadCatalogFromXmlAsync()
    {
        if (_isLoadingCatalog)
            return;

        var xmlPath = _xmlPath.Text.Trim();
        if (!File.Exists(xmlPath))
        {
            _catalogStatus.Text = "Catálogo não carregado.";
            _folder.Items.Clear();
            _tasks.Items.Clear();
            _allTasks.Clear();
            _allComponents.Clear();
            _allReferences.Clear();
            PopulateProjectReferenceGrid();
            return;
        }

        _isLoadingCatalog = true;
        _catalogStatus.Text = "Carregando catálogo do XML...";
        try
        {
            var catalog = await Task.Run(() => XmlCatalogService.Load(xmlPath));
            _folder.BeginUpdate();
            _folder.Items.Clear();
            foreach (var folder in catalog.Folders)
                _folder.Items.Add(folder);
            _folder.EndUpdate();

            _allTasks = catalog.Tasks;
            _allComponents = catalog.Components;
            _allReferences = catalog.References;
            PopulateProjectReferenceGrid();
            ApplyTaskFilter();
            var xpaRefCount = catalog.References.Count(r => string.Equals(r.Kind, "XPA", StringComparison.OrdinalIgnoreCase));
            var dotNetRefCount = catalog.References.Count - xpaRefCount;
            _catalogStatus.Text = $"Catálogo carregado: {catalog.Folders.Count} pastas, {catalog.Tasks.Count} tasks, {xpaRefCount} componentes XPA, {dotNetRefCount} refs .NET/DLL.";
        }
        catch (Exception ex)
        {
            _catalogStatus.Text = "Falha ao carregar catálogo.";
            AppendLogLine(ex.ToString());
        }
        finally
        {
            _isLoadingCatalog = false;
        }
    }

    private void ApplyTaskFilter()
    {
        var selected = GetSelectedTasks().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var filter = _taskFilter.Text.Trim();
        var visibleTasks = string.IsNullOrWhiteSpace(filter)
            ? _allTasks
            : _allTasks.Where(t => t.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        _tasks.BeginUpdate();
        _tasks.Items.Clear();
        foreach (var task in visibleTasks)
        {
            var index = _tasks.Items.Add(task);
            if (selected.Contains(task))
                _tasks.SetItemChecked(index, true);
        }
        _tasks.EndUpdate();
        RefreshCommandPreview();
    }

    private void RefreshUiState()
    {
        _folder.Enabled = _modeFolder.Checked;
        _taskRanges.Enabled = _modeTaskRange.Checked;
        _taskFilter.Enabled = _modeTask.Checked;
        _tasks.Enabled = _modeTask.Checked;
        _withDependencies.Enabled = _modeTask.Checked;
        if (!_modeTask.Checked)
            _withDependencies.Checked = false;
        var partialTaskScope = _modeTask.Checked || _modeTaskRange.Checked;
        _incrementalOutput.Enabled = partialTaskScope;
        _internalCompat.Enabled = partialTaskScope && !_withDependencies.Checked;
        _internalCompat.Checked = partialTaskScope && !_withDependencies.Checked;
        _fullSolution.Enabled = !_modeFolder.Checked;
        _tablesXmls.Enabled = _modeFolder.Checked || partialTaskScope;
        _runtimeCoreDllPath.Enabled = string.Equals(_runtimeCoreReferenceMode.SelectedItem?.ToString(), "Dll", StringComparison.OrdinalIgnoreCase);
        var performanceEnabled = _parallelTaskGeneration.Checked;
        _dynamicWorkers.Enabled = performanceEnabled;
        _parallelMaxWorkers.Enabled = performanceEnabled;
        _parallelMinWorkers.Enabled = performanceEnabled;
        _parallelInitialWorkers.Enabled = performanceEnabled;
        _parallelInitialUntilPercent.Enabled = performanceEnabled && _dynamicWorkers.Checked;
        _parallelMaxMemory.Enabled = performanceEnabled && _dynamicWorkers.Checked;
        _cacheResetEveryTasks.Enabled = performanceEnabled;
        RefreshCommandPreview();
    }

    private ConversionOptions BuildOptions()
    {
        var options = new ConversionOptions
        {
            XmlPath = _xmlPath.Text.Trim(),
            OutputDir = _outputDir.Text.Trim(),
            AppNamespace = string.IsNullOrWhiteSpace(_namespace.Text) ? null : _namespace.Text.Trim(),
            OutputType = _outputType.SelectedItem?.ToString() ?? "WinExe",
            RuntimeCoreReferenceMode = _runtimeCoreReferenceMode.SelectedItem?.ToString() ?? "Project",
            RuntimeCoreDllPath = string.IsNullOrWhiteSpace(_runtimeCoreDllPath.Text) ? null : _runtimeCoreDllPath.Text.Trim(),
            RuntimeRootPath = string.IsNullOrWhiteSpace(_runtimeRootPath.Text) ? null : _runtimeRootPath.Text.Trim(),
            FolderFilter = _modeFolder.Checked && _folder.SelectedItem is string folder ? folder : null,
            WithDependencies = _modeTask.Checked && _withDependencies.Checked,
            FullSolution = !_modeFolder.Checked && _fullSolution.Checked,
            ParallelTaskGeneration = _parallelTaskGeneration.Checked,
            IncrementalOutput = (_modeTask.Checked || _modeTaskRange.Checked) && _incrementalOutput.Checked
        };

        foreach (var path in SplitLines(_componentXmls.Text))
            options.ComponentXmlPaths.Add(path);

        foreach (DataGridViewRow row in _projectRefMap.Rows)
        {
            var kind = row.Cells["Kind"].Value?.ToString()?.Trim();
            var component = row.Cells["Reference"].Value?.ToString()?.Trim();
            var path = row.Cells["Project"].Value?.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(component) || string.IsNullOrWhiteSpace(path))
                continue;

            if (string.Equals(kind, ".NET/DLL", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                options.DllReferenceMap[component] = path;
            else
                options.ProjectReferenceMap[component] = path;
        }

        if (_modeFolder.Checked || _modeTask.Checked || _modeTaskRange.Checked)
        {
            foreach (var path in SplitLines(_tablesXmls.Text))
                options.TablesXmlPaths.Add(path);
        }

        options.DisableImplicitComponentXmlResolution =
            options.ProjectReferenceMap.Count > 0 ||
            options.DllReferenceMap.Count > 0 ||
            ((_modeFolder.Checked || _modeTask.Checked || _modeTaskRange.Checked) &&
             (options.TablesXmlPaths.Count > 0 || options.ComponentXmlPaths.Count > 0));

        if (_modeTask.Checked)
        {
            foreach (var task in GetSelectedTasks())
                options.TaskFilters.Add(task);
        }

        if (_modeTaskRange.Checked)
        {
            foreach (var range in GetTaskRanges(ignoreInvalid: true, out _))
                options.TaskRanges.Add(range);
        }

        return options;
    }

    private List<string> GetSelectedTasks()
    {
        return _tasks.CheckedItems.Cast<object>()
            .Select(i => i.ToString())
            .Where(i => !string.IsNullOrWhiteSpace(i))
            .Select(i => i!)
            .ToList();
    }

    private List<TopLevelTaskRange> GetTaskRanges(bool ignoreInvalid, out string? error)
    {
        error = null;
        var ranges = new List<TopLevelTaskRange>();
        var tokens = _taskRanges.Text.Split(new[] { ';', ',', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var token in tokens)
        {
            if (TopLevelTaskRange.TryParse(token, out var range))
            {
                ranges.Add(range);
                continue;
            }

            if (ignoreInvalid)
                continue;

            error = $"Range de task invalido: {token}";
            return ranges;
        }

        return ranges;
    }

    private void RefreshCommandPreview()
    {
        var options = BuildOptions();
        var args = new List<string>
        {
            "XpaConverterMvp",
            Quote(options.XmlPath),
            Quote(options.OutputDir)
        };

        if (!string.IsNullOrWhiteSpace(options.AppNamespace))
            args.Add(Quote(options.AppNamespace));
        args.Add("--output-type");
        args.Add(Quote(options.OutputType));
        args.Add("--runtime-core-ref");
        args.Add(Quote(options.RuntimeCoreReferenceMode));
        if (!string.IsNullOrWhiteSpace(options.RuntimeCoreDllPath))
        {
            args.Add("--runtime-core-dll");
            args.Add(Quote(options.RuntimeCoreDllPath));
        }
        if (!string.IsNullOrWhiteSpace(options.RuntimeRootPath))
        {
            args.Add("--runtime-root");
            args.Add(Quote(options.RuntimeRootPath));
        }
        if (!string.IsNullOrWhiteSpace(options.FolderFilter))
        {
            args.Add("--folder");
            args.Add(Quote(options.FolderFilter));
        }
        foreach (var task in options.TaskFilters)
        {
            args.Add("--task");
            args.Add(Quote(task));
        }
        foreach (var range in options.TaskRanges)
        {
            args.Add("--task-range");
            args.Add(Quote(range.ToString()));
        }
        foreach (var path in options.ComponentXmlPaths)
        {
            args.Add("--component-xml");
            args.Add(Quote(path));
        }
        foreach (var path in options.TablesXmlPaths)
        {
            args.Add("--tables-xml");
            args.Add(Quote(path));
        }
        foreach (var projectRef in options.ProjectReferenceMap.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            args.Add("--project-ref-map");
            args.Add(Quote($"{projectRef.Key}={projectRef.Value}"));
        }
        foreach (var dllRef in options.DllReferenceMap.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            args.Add("--dll-ref-map");
            args.Add(Quote($"{dllRef.Key}={dllRef.Value}"));
        }
        if (options.DisableImplicitComponentXmlResolution)
            args.Add("--no-implicit-component-xml");
        if (options.WithDependencies)
            args.Add("--with-dependencies");
        if (options.FullSolution)
            args.Add("--full-solution");
        if (options.ParallelTaskGeneration)
            args.Add("--parallel-tasks");
        if (options.IncrementalOutput)
            args.Add("--incremental-output");

        var envPreview = BuildPerformanceEnvironment(options)
            .Select(kv => $"set {kv.Key}={kv.Value}")
            .ToList();
        envPreview.Add(string.Join(" ", args));
        _commandPreview.Text = string.Join(Environment.NewLine, envPreview);
    }

    private async Task RunConversionAsync()
    {
        try
        {
            var options = BuildOptions();
            if (string.IsNullOrWhiteSpace(options.XmlPath) || string.IsNullOrWhiteSpace(options.OutputDir))
            {
                MessageBox.Show(this, "XML e Output são obrigatórios.", "Validação", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (_modeFolder.Checked && string.IsNullOrWhiteSpace(options.FolderFilter))
            {
                MessageBox.Show(this, "Selecione a pasta XPA.", "Validação", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (_modeTask.Checked && options.TaskFilters.Count == 0)
            {
                MessageBox.Show(this, "Selecione ao menos uma task.", "Validação", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (_modeTaskRange.Checked)
            {
                var ranges = GetTaskRanges(ignoreInvalid: false, out var rangeError);
                if (!string.IsNullOrWhiteSpace(rangeError))
                {
                    MessageBox.Show(this, rangeError, "Validação", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                if (ranges.Count == 0)
                {
                    MessageBox.Show(this, "Informe ao menos um range de task.", "Validação", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                options.TaskRanges.Clear();
                options.TaskRanges.AddRange(ranges);
            }

            var performanceEnvironment = BuildPerformanceEnvironment(options);
            SaveUserPreferences();
            _log.Clear();
            AppendLogLine($"Build configuration: {BuildConfigurationLabel}");
            if (options.ParallelTaskGeneration)
            {
                ApplyPerformanceEnvironment(performanceEnvironment);
                AppendLogLine("Paralelismo experimental ativado: as tasks serão emitidas em paralelo com caches isolados por worker.");
                AppendLogLine("Performance: " + string.Join("; ", performanceEnvironment.Select(kv => $"{kv.Key}={kv.Value}")));
            }
            if (options.IncrementalOutput)
                AppendLogLine("Modo incremental ativado: o output existente será preservado e os arquivos desta execução serão sobrescritos quando tiverem o mesmo nome.");
#if DEBUG
            AppendLogLine("Aviso: a GUI está rodando em Debug. Para medir performance real, prefira buildar/executar em Release.");
#endif
            if ((_modeTask.Checked || _modeTaskRange.Checked || _modeFolder.Checked) && !options.FullSolution)
                AppendLogLine("Aviso: sem 'Gerar solução completa', o output não materializa XPARuntimeCore, XPARuntimeCore.Box.dll nem lib.");
            ToggleRunningState(true);

            var writer = new UiTextWriter(AppendLogLine);
            var exitCode = await Task.Run(() => ConversionRunner.Run(options, writer, writer));
            AppendLogLine($"ExitCode: {exitCode}");
        }
        catch (Exception ex)
        {
            AppendLogLine("Falha durante a conversão:");
            AppendLogLine(ex.ToString());
            MessageBox.Show(this, ex.Message, "Erro na conversão", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            ToggleRunningState(false);
        }
    }

    private Dictionary<string, string> BuildPerformanceEnvironment(ConversionOptions options)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        env["XPA_CONVERTER_TELEMETRY_LEVEL"] = "minimal";
        if (!options.ParallelTaskGeneration)
            return env;

        var maxWorkers = Math.Max(1, (int)_parallelMaxWorkers.Value);
        var minWorkers = Math.Clamp((int)_parallelMinWorkers.Value, 1, maxWorkers);
        var initialWorkers = Math.Clamp((int)_parallelInitialWorkers.Value, minWorkers, maxWorkers);
        var initialUntil = ((double)_parallelInitialUntilPercent.Value / 100d).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        var maxMemory = NormalizeMemoryLimit(_parallelMaxMemory.Text, "90%");

        env["XPA_CONVERTER_PARALLEL_TASK_DEGREE"] = maxWorkers.ToString(System.Globalization.CultureInfo.InvariantCulture);
        env["XPA_CONVERTER_PARALLEL_DYNAMIC_WORKERS"] = _dynamicWorkers.Checked ? "true" : "false";
        env["XPA_CONVERTER_PARALLEL_MIN_WORKERS"] = minWorkers.ToString(System.Globalization.CultureInfo.InvariantCulture);
        env["XPA_CONVERTER_PARALLEL_RAMP_INITIAL_WORKERS"] = initialWorkers.ToString(System.Globalization.CultureInfo.InvariantCulture);
        env["XPA_CONVERTER_PARALLEL_RAMP_INITIAL_UNTIL"] = initialUntil;
        env["XPA_CONVERTER_PARALLEL_DYNAMIC_SOFT_GB"] = ScaleMemoryLimit(maxMemory, 0.64);
        env["XPA_CONVERTER_PARALLEL_DYNAMIC_MEDIUM_GB"] = ScaleMemoryLimit(maxMemory, 0.80);
        env["XPA_CONVERTER_PARALLEL_DYNAMIC_HARD_GB"] = ScaleMemoryLimit(maxMemory, 0.92);
        env["XPA_CONVERTER_CACHE_RESET_MEMORY_GB"] = ScaleMemoryLimit(maxMemory, 0.80);
        env["XPA_CONVERTER_CACHE_RESET_EVERY_TASKS"] = ((int)_cacheResetEveryTasks.Value).ToString(System.Globalization.CultureInfo.InvariantCulture);
        env["XPA_CONVERTER_GC_AFTER_TASK_MEMORY_GB"] = ScaleMemoryLimit(maxMemory, 0.92);
        env["XPA_CONVERTER_PARALLEL_MEMORY_PAUSE_GB"] = maxMemory;
        return env;
    }

    private static void ApplyPerformanceEnvironment(Dictionary<string, string> environment)
    {
        foreach (var item in environment)
            Environment.SetEnvironmentVariable(item.Key, item.Value);
    }

    private static string NormalizeMemoryLimit(string raw, string fallback)
    {
        raw = raw.Trim();
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;

        if (raw.EndsWith("%", StringComparison.Ordinal))
        {
            var percentText = raw[..^1].Trim();
            if (double.TryParse(percentText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var percent) && percent >= 0)
                return percent.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "%";
            return fallback;
        }

        if (double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) && value >= 0)
            return value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

        return fallback;
    }

    private static string ScaleMemoryLimit(string normalized, double factor)
    {
        if (normalized.EndsWith("%", StringComparison.Ordinal))
        {
            var percent = double.Parse(normalized[..^1], System.Globalization.CultureInfo.InvariantCulture);
            return (percent * factor).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "%";
        }

        var value = double.Parse(normalized, System.Globalization.CultureInfo.InvariantCulture);
        return (value * factor).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    }
    private void ToggleRunningState(bool isRunning)
    {
        _run.Enabled = !isRunning;
        UseWaitCursor = isRunning;
    }

    private void AppendLogLine(string text)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action<string>(AppendLogLine), text);
            return;
        }

        _log.AppendText(text + Environment.NewLine);
    }

    private void BrowseXml(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "XML (*.xml)|*.xml|Todos (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            _xmlPath.Text = dialog.FileName;
    }

    private void BrowseOutput(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            ShowNewFolderButton = true
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            _outputDir.Text = dialog.SelectedPath;
    }

    private void BrowseMultiXmlInto(TextBox target)
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "XML (*.xml)|*.xml|Todos (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        var existing = SplitLines(target.Text).ToList();
        foreach (var file in dialog.FileNames)
        {
            if (!existing.Contains(file, StringComparer.OrdinalIgnoreCase))
                existing.Add(file);
        }

        target.Text = string.Join(Environment.NewLine, existing);
    }

    private void BrowseMultiProjectInto(TextBox target)
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "Projetos C# (*.csproj)|*.csproj|Todos (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        var existing = SplitLines(target.Text).ToList();
        foreach (var file in dialog.FileNames)
        {
            if (!existing.Contains(file, StringComparer.OrdinalIgnoreCase))
                existing.Add(file);
        }

        target.Text = string.Join(Environment.NewLine, existing);
    }

    private void InitializeProjectRefGrid()
    {
        if (_projectRefMap.Columns.Count > 0)
            return;

        _projectRefMap.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Kind",
            HeaderText = "Tipo",
            ReadOnly = true,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = 16
        });
        _projectRefMap.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Reference",
            HeaderText = "Referência XML",
            ReadOnly = true,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = 34
        });
        _projectRefMap.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "XmlHint",
            HeaderText = "Origem XML",
            ReadOnly = true,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = 25
        });
        _projectRefMap.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Project",
            HeaderText = "Projeto .NET ou DLL",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = 25
        });
        _projectRefMap.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "AssemblyIdentity",
            HeaderText = "Assembly real",
            ReadOnly = true,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = 20
        });
    }

    private void PopulateProjectReferenceGrid()
    {
        var existing = GetProjectReferenceMappings();
        _projectRefMap.Rows.Clear();
        var references = _allReferences.Count > 0
            ? _allReferences
            : _allComponents
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .Select(x => new XmlCatalogReference(x, "XPA", null))
                .ToList();

        foreach (var reference in FilterReferences(references, existing).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            existing.TryGetValue(reference.Name, out var path);
            var rowIndex = _projectRefMap.Rows.Add(reference.Kind, reference.Name, reference.XmlHint ?? "", path ?? "", TryResolveAssemblyIdentity(path) ?? "");
            RefreshReferenceMetadataRow(_projectRefMap.Rows[rowIndex]);
        }
        if (_projectRefMap.Rows.Count > 0)
        {
            _projectRefMap.CurrentCell = _projectRefMap.Rows[0].Cells["Project"];
            _projectRefMap.Rows[0].Selected = true;
        }
        RefreshCommandPreview();
    }

    private Dictionary<string, string> GetProjectReferenceMappings()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DataGridViewRow row in _projectRefMap.Rows)
        {
            var component = row.Cells["Reference"].Value?.ToString()?.Trim();
            var path = row.Cells["Project"].Value?.ToString()?.Trim();
            if (!string.IsNullOrWhiteSpace(component) && !string.IsNullOrWhiteSpace(path))
                result[component] = path;
        }
        return result;
    }

    private IEnumerable<XmlCatalogReference> FilterReferences(IEnumerable<XmlCatalogReference> references, IReadOnlyDictionary<string, string> existingMappings)
    {
        var filter = _referenceFilter.SelectedItem?.ToString() ?? "Todas";
        return filter switch
        {
            "Somente XPA" => references.Where(r => string.Equals(r.Kind, "XPA", StringComparison.OrdinalIgnoreCase)),
            "Somente .NET/DLL" => references.Where(r => string.Equals(r.Kind, ".NET/DLL", StringComparison.OrdinalIgnoreCase)),
            "Somente não mapeadas" => references.Where(r => !existingMappings.ContainsKey(r.Name)),
            _ => references
        };
    }

    private void BrowseProjectForSelectedComponent()
    {
        var row = GetActiveProjectReferenceRow();
        if (row is null)
        {
            MessageBox.Show(this, "Nenhuma referência disponível para mapear.", "Mapeamento", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var kind = row.Cells["Kind"].Value?.ToString();
        var filter = string.Equals(kind, ".NET/DLL", StringComparison.OrdinalIgnoreCase)
            ? "Assemblies (*.dll)|*.dll|Todos (*.*)|*.*"
            : "Projetos ou DLL (*.csproj;*.dll)|*.csproj;*.dll|Projetos C# (*.csproj)|*.csproj|Assemblies (*.dll)|*.dll|Todos (*.*)|*.*";

        using var dialog = new OpenFileDialog
        {
            Filter = filter,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        row.Cells["Project"].Value = dialog.FileName;
        RefreshReferenceMetadataRow(row);
        RefreshCommandPreview();
    }

    private async Task AutoMapVisibleReferencesAsync(Button autoMapButton)
    {
        var pending = new List<(DataGridViewRow Row, string? Kind, string? Name, string? XmlHint)>();
        foreach (DataGridViewRow row in _projectRefMap.Rows)
        {
            if (row.IsNewRow)
                continue;

            var current = row.Cells["Project"].Value?.ToString();
            if (!string.IsNullOrWhiteSpace(current))
                continue;

            var kind = row.Cells["Kind"].Value?.ToString();
            var name = row.Cells["Reference"].Value?.ToString();
            var xmlHint = row.Cells["XmlHint"].Value?.ToString();
            pending.Add((row, kind, name, xmlHint));
        }

        if (pending.Count == 0)
        {
            MessageBox.Show(this, "Nenhuma referÃªncia visÃ­vel precisa ser mapeada.", "Auto map", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // Capture all values owned by WinForms before leaving the UI thread.
        // Recursive filesystem discovery can be slow and must never read a
        // TextBox/DataGridView from the worker thread.
        var roots = EnumerateReferenceRoots().ToArray();
        var requests = pending
            .Select(item => (item.Kind, item.Name, item.XmlHint))
            .ToArray();

        var oldText = autoMapButton.Text;
        autoMapButton.Enabled = false;
        autoMapButton.Text = "Mapeando...";
        UseWaitCursor = true;

        string?[] resolvedPaths;
        try
        {
            resolvedPaths = await Task.Run(() =>
            {
                var result = new string?[requests.Length];
                var recursiveIndex = new Lazy<ReferenceCandidateIndex>(
                    () => BuildReferenceCandidateIndex(roots),
                    LazyThreadSafetyMode.ExecutionAndPublication);
                for (var index = 0; index < requests.Length; index++)
                {
                    var request = requests[index];
                    result[index] = ResolveReferenceCandidate(
                        request.Kind,
                        request.Name,
                        request.XmlHint,
                        roots,
                        recursiveIndex);
                }
                return result;
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"Falha ao pesquisar projetos e DLLs:{Environment.NewLine}{ex.Message}",
                "Auto map",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }
        finally
        {
            UseWaitCursor = false;
            autoMapButton.Text = oldText;
            autoMapButton.Enabled = true;
        }

        var updated = 0;
        for (var index = 0; index < pending.Count; index++)
        {
            var resolved = resolvedPaths[index];
            if (string.IsNullOrWhiteSpace(resolved))
                continue;

            var row = pending[index].Row;
            row.Cells["Project"].Value = resolved;
            RefreshReferenceMetadataRow(row);
            updated++;
        }

        RefreshCommandPreview();
        var message = updated == 0
            ? "Nenhuma referência visível foi resolvida automaticamente."
            : $"{updated} referência(s) foram mapeadas automaticamente.";
        MessageBox.Show(this, message, "Auto map", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ClearSelectedProjectMapping()
    {
        var row = GetActiveProjectReferenceRow();
        if (row is null)
            return;

        row.Cells["Project"].Value = "";
        RefreshReferenceMetadataRow(row);
        RefreshCommandPreview();
    }

    private void BrowseRuntimeCoreDll()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "Assemblies (*.dll)|*.dll|Todos (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        _runtimeCoreDllPath.Text = dialog.FileName;
    }

    private void BrowseRuntimeRoot()
    {
        using var dialog = new FolderBrowserDialog
        {
            ShowNewFolderButton = false
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        _runtimeRootPath.Text = dialog.SelectedPath;
        if (string.IsNullOrWhiteSpace(_runtimeCoreDllPath.Text))
        {
            var preferred = Path.Combine(dialog.SelectedPath, "XPARuntimeCore.dll");
            if (File.Exists(preferred))
                _runtimeCoreDllPath.Text = preferred;
        }
    }

    private DataGridViewRow? GetActiveProjectReferenceRow()
    {
        if (_projectRefMap.CurrentRow is not null && !_projectRefMap.CurrentRow.IsNewRow)
            return _projectRefMap.CurrentRow;

        if (_projectRefMap.SelectedRows.Count > 0)
            return _projectRefMap.SelectedRows[0];

        foreach (DataGridViewRow row in _projectRefMap.Rows)
        {
            if (row.IsNewRow)
                continue;
            _projectRefMap.CurrentCell = row.Cells["Project"];
            row.Selected = true;
            return row;
        }

        return null;
    }

    private void ProjectRefMapOnCellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _projectRefMap.Rows.Count)
            return;

        var row = _projectRefMap.Rows[e.RowIndex];
        var mappedPath = row.Cells["Project"].Value?.ToString();
        var kind = row.Cells["Kind"].Value?.ToString();
        var referenceName = row.Cells["Reference"].Value?.ToString();
        var assemblyIdentity = row.Cells["AssemblyIdentity"].Value?.ToString();
        if (string.IsNullOrWhiteSpace(mappedPath))
        {
            e.CellStyle.BackColor = Color.FromArgb(255, 248, 220);
            return;
        }

        if (!File.Exists(mappedPath))
        {
            e.CellStyle.BackColor = Color.MistyRose;
            return;
        }

        if (string.Equals(kind, ".NET/DLL", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(referenceName) &&
            !string.IsNullOrWhiteSpace(assemblyIdentity) &&
            !string.Equals(referenceName, assemblyIdentity, StringComparison.OrdinalIgnoreCase))
        {
            e.CellStyle.BackColor = Color.LightYellow;
            row.Cells["AssemblyIdentity"].ToolTipText = $"XML='{referenceName}' DLL='{assemblyIdentity}'";
            return;
        }

        e.CellStyle.BackColor = Color.Honeydew;
    }

    private void RefreshReferenceMetadataRow(DataGridViewRow row)
    {
        if (row.IsNewRow)
            return;

        var mappedPath = row.Cells["Project"].Value?.ToString();
        row.Cells["AssemblyIdentity"].Value = TryResolveAssemblyIdentity(mappedPath) ?? "";
    }

    private static string? TryResolveAssemblyIdentity(string? mappedPath)
    {
        if (string.IsNullOrWhiteSpace(mappedPath) ||
            !mappedPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(mappedPath))
            return null;

        try
        {
            return System.Reflection.AssemblyName.GetAssemblyName(mappedPath).Name;
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveReferenceCandidate(
        string? kind,
        string? name,
        string? xmlHint,
        IReadOnlyList<string> referenceRoots,
        Lazy<ReferenceCandidateIndex> recursiveIndex)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(xmlHint))
        {
            var normalizedHint = xmlHint.Trim();
            if (Path.IsPathRooted(normalizedHint) &&
                File.Exists(normalizedHint) &&
                IsSupportedReferenceMapPath(normalizedHint))
                return normalizedHint;

            if (normalizedHint.StartsWith("%WorkingDir%", StringComparison.OrdinalIgnoreCase))
            {
                var relative = normalizedHint["%WorkingDir%".Length..].TrimStart('\\', '/')
                    .Replace('\\', Path.DirectorySeparatorChar)
                    .Replace('/', Path.DirectorySeparatorChar);
                foreach (var root in referenceRoots)
                    candidates.Add(Path.Combine(root, relative));
            }
        }

        var isDotNetReference = string.Equals(kind, ".NET/DLL", StringComparison.OrdinalIgnoreCase);
        var isXpaReference = string.Equals(kind, "XPA", StringComparison.OrdinalIgnoreCase);
        if (isDotNetReference || isXpaReference)
        {
            var fileName = name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? name : name + ".dll";
            foreach (var root in referenceRoots)
            {
                if (isXpaReference)
                {
                    var projectName = Path.GetFileNameWithoutExtension(name) + ".csproj";
                    candidates.Add(Path.Combine(root, projectName));
                    candidates.Add(Path.Combine(root, Path.GetFileNameWithoutExtension(name), projectName));
                }
                candidates.Add(Path.Combine(root, fileName));
                candidates.Add(Path.Combine(root, "Resources", fileName));
                candidates.Add(Path.Combine(root, "Resources", "PushSharp", fileName));
            }

            var exactMatch = candidates.FirstOrDefault(File.Exists);
            if (!string.IsNullOrWhiteSpace(exactMatch))
                return exactMatch;

            if (isXpaReference)
            {
                var recursiveProjectMatch = ResolveIndexedReference(
                    recursiveIndex.Value.ProjectByName,
                    name,
                    xmlHint);
                if (!string.IsNullOrWhiteSpace(recursiveProjectMatch))
                    return recursiveProjectMatch;
            }

            var recursiveMatch = ResolveIndexedReference(
                recursiveIndex.Value.DllByName,
                name,
                xmlHint);
            if (!string.IsNullOrWhiteSpace(recursiveMatch))
                return recursiveMatch;
        }

        return candidates.FirstOrDefault(path => File.Exists(path) && IsSupportedReferenceMapPath(path));
    }

    private sealed record ReferenceCandidateIndex(
        IReadOnlyDictionary<string, string> ProjectByName,
        IReadOnlyDictionary<string, string> DllByName);

    private static ReferenceCandidateIndex BuildReferenceCandidateIndex(IReadOnlyList<string> referenceRoots)
    {
        var projects = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dlls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var visitedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in referenceRoots)
        {
            if (!Directory.Exists(root))
                continue;

            try
            {
                foreach (var projectPath in Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories))
                {
                    if (!visitedFiles.Add(projectPath))
                        continue;
                    var projectName = Path.GetFileNameWithoutExtension(projectPath);
                    projects.TryAdd(projectName, projectPath);
                }
            }
            catch
            {
            }

            try
            {
                foreach (var dllPath in Directory.EnumerateFiles(root, "*.dll", SearchOption.AllDirectories))
                {
                    if (!visitedFiles.Add(dllPath))
                        continue;

                    var fileName = Path.GetFileNameWithoutExtension(dllPath);
                    dlls.TryAdd(fileName, dllPath);

                    var assemblyIdentity = TryResolveAssemblyIdentity(dllPath);
                    if (!string.IsNullOrWhiteSpace(assemblyIdentity))
                        dlls.TryAdd(assemblyIdentity, dllPath);
                }
            }
            catch
            {
            }
        }

        return new ReferenceCandidateIndex(projects, dlls);
    }

    private static string? ResolveIndexedReference(
        IReadOnlyDictionary<string, string> indexedPaths,
        string referenceName,
        string? xmlHint)
    {
        var lookupNames = BuildDllLookupNames(referenceName, xmlHint);
        foreach (var lookupName in lookupNames)
        {
            if (indexedPaths.TryGetValue(lookupName, out var path))
                return path;
        }

        return null;
    }

    private static bool IsSupportedReferenceMapPath(string path)
        => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
           path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

    private static HashSet<string> BuildDllLookupNames(string referenceName, string? xmlHint)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddLookupToken(result, referenceName);
        AddLookupToken(result, Path.GetFileNameWithoutExtension(referenceName));

        if (!string.IsNullOrWhiteSpace(xmlHint))
        {
            var normalizedHint = xmlHint.Trim();
            AddLookupToken(result, normalizedHint);
            AddLookupToken(result, Path.GetFileNameWithoutExtension(normalizedHint));

            var commaIndex = normalizedHint.IndexOf(',');
            if (commaIndex > 0)
                AddLookupToken(result, normalizedHint[..commaIndex]);
        }

        return result;
    }

    private static void AddLookupToken(ISet<string> lookupNames, string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
            return;

        var candidate = rawValue.Trim().Trim('"');
        if (candidate.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            candidate = Path.GetFileNameWithoutExtension(candidate);
        if (candidate.StartsWith("%WorkingDir%", StringComparison.OrdinalIgnoreCase))
            candidate = Path.GetFileNameWithoutExtension(candidate);

        candidate = Path.GetFileNameWithoutExtension(Path.GetFileName(candidate));
        if (string.IsNullOrWhiteSpace(candidate))
            return;

        lookupNames.Add(candidate);
    }

    private IEnumerable<string> EnumerateReferenceRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in new[]
                 {
                     _runtimeRootPath.Text.Trim(),
                     Path.Combine(@"D:\DLLs"),
                     Path.Combine(Directory.GetCurrentDirectory(), "DLLs"),
                     Path.Combine(Directory.GetCurrentDirectory(), "Resources")
                 })
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            var fullPath = Path.GetFullPath(candidate);
            if (seen.Add(fullPath))
                yield return fullPath;
        }
    }

    private static IEnumerable<string> SplitLines(string raw)
    {
        return raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x));
    }

    private static string Quote(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "\"\"";
        return value.Contains(' ') ? $"\"{value}\"" : value;
    }

    private sealed class UiTextWriter(Action<string> sink) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string? value)
        {
            if (!string.IsNullOrEmpty(value))
                sink(value);
        }

        public override void Write(char value)
        {
        }
    }
}

