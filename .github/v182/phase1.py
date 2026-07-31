from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[2]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8")


def write(path: str, text: str) -> None:
    (ROOT / path).write_text(text, encoding="utf-8", newline="")


def replace_once(path: str, old: str, new: str) -> None:
    text = read(path)
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{path}: expected one literal match, found {count}: {old[:100]!r}")
    write(path, text.replace(old, new, 1))


def regex_once(path: str, pattern: str, replacement: str) -> None:
    text = read(path)
    updated, count = re.subn(pattern, replacement, text, count=1, flags=re.S)
    if count != 1:
        raise RuntimeError(f"{path}: expected one regex match, found {count}: {pattern[:100]!r}")
    write(path, updated)


# Quest status: Available remains only as a persisted legacy enum value. It is
# never returned or displayed by the application.
replace_once(
    "TarkovHelper/Models/QuestStatus.cs",
    """        /// <summary>\n        /// Start conditions are met, but the quest has not been explicitly started.\n        /// </summary>\n        Available\n""",
    """        /// <summary>\n        /// Legacy persisted value from v1.8.1. Runtime status evaluation normalizes\n        /// this value to Active and the UI never exposes it.\n        /// </summary>\n        [Obsolete(\"Available is a legacy persisted value; eligible quests are Active.\")]\n        Available\n""",
)

replace_once(
    "TarkovHelper/Services/ActualQuestStatusEvaluator.cs",
    """/// Calculates the status exposed by the helper. Start conditions determine\n/// whether a quest is available, while Active is reserved for a quest that was\n/// explicitly started and persisted in user progress.\n""",
    """/// Calculates the status exposed by the helper. Every quest whose start\n/// conditions are satisfied is treated as Active because the helper assumes\n/// all quests available in game have already been accepted.\n""",
)
replace_once(
    "TarkovHelper/Services/ActualQuestStatusEvaluator.cs",
    """            return Cache(\n                taskKey,\n                storedStatus == QuestStatus.Active ? QuestStatus.Active : QuestStatus.Available);\n""",
    """            return Cache(taskKey, QuestStatus.Active);\n""",
)

regex_once(
    "TarkovHelper/Services/QuestProgressService.cs",
    r"        public QuestStatus GetStatus\(TarkovTask task\)\n        \{.*?\n        \}\n\n        /// <summary>\n        /// Check if player level meets quest requirement",
    """        public QuestStatus GetStatus(TarkovTask task)\n        {\n            var taskId = task.Ids?.FirstOrDefault();\n            var taskKey = taskId ?? task.NormalizedName;\n\n            if (string.IsNullOrEmpty(taskKey))\n                return QuestStatus.Active;\n\n            QuestStatus? persistedStatus = null;\n            if (!string.IsNullOrEmpty(taskId) && _questProgress.TryGetValue(taskId, out var statusById))\n            {\n                persistedStatus = statusById;\n            }\n            else if (!string.IsNullOrEmpty(task.NormalizedName) &&\n                     _questProgress.TryGetValue(task.NormalizedName, out var statusByName))\n            {\n                persistedStatus = statusByName;\n            }\n\n            if (persistedStatus is QuestStatus.Done or QuestStatus.Failed)\n                return persistedStatus.Value;\n\n            bool isTopLevel = _getStatusVisited == null;\n            _getStatusVisited ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);\n\n            if (!_getStatusVisited.Add(taskKey))\n                return QuestStatus.Locked;\n\n            try\n            {\n                if (!IsEditionRequirementMet(task) ||\n                    !IsPrestigeLevelRequirementMet(task) ||\n                    !IsFactionRequirementMet(task))\n                {\n                    return QuestStatus.Unavailable;\n                }\n\n                if (!IsDspRequirementMet(task) || !ArePrerequisitesMet(task))\n                    return QuestStatus.Locked;\n\n                if (!IsLevelRequirementMet(task) || !IsScavKarmaRequirementMet(task))\n                    return QuestStatus.LevelLocked;\n\n                // Eligible quests are always considered accepted and in progress.\n                // Legacy Available progress rows are intentionally normalized here.\n                return QuestStatus.Active;\n            }\n            finally\n            {\n                _getStatusVisited.Remove(taskKey);\n                if (isTopLevel)\n                    _getStatusVisited = null;\n            }\n        }\n\n        /// <summary>\n        /// Check if player level meets quest requirement""",
)

regex_once(
    "TarkovHelper/Services/QuestProgressService.cs",
    r"        /// <summary>\n        /// Mark an eligible quest as explicitly started\.\n        /// </summary>\n        public bool StartQuest\(TarkovTask task\)\n        \{.*?\n        \}\n\n        /// <summary>\n        /// Mark quest as completed",
    """        /// <summary>\n        /// Compatibility entry point for imported log events. Eligible quests are\n        /// already Active, so no separate accepted state is persisted.\n        /// </summary>\n        public bool StartQuest(TarkovTask task) => GetStatus(task) == QuestStatus.Active;\n\n        /// <summary>\n        /// Mark quest as completed""",
)

# Quest list UI and behavior.
replace_once(
    "TarkovHelper/Pages/QuestListPage.xaml",
    "                            Content=\"{Binding ActionButtonText}\" Padding=\"8,4\"",
    "                            Content=\"완료\" Padding=\"8,4\"",
)
replace_once(
    "TarkovHelper/Pages/QuestListPage.xaml",
    "                        <ComboBoxItem Content=\"수주 가능\" Tag=\"Available\"/>\n",
    "",
)
replace_once(
    "TarkovHelper/Pages/QuestListViewModels.cs",
    "        public string ActionButtonText { get; set; } = \"완료\";\n",
    "",
)
replace_once(
    "TarkovHelper/Pages/QuestListPage.xaml.cs",
    """            _traders = tasks.Select(t => t.Trader).Where(t => !string.IsNullOrEmpty(t)).Distinct().OrderBy(t => t).ToList();\n            _maps = tasks.Where(t => t.Maps != null).SelectMany(t => t.Maps!).Distinct().OrderBy(m => m).ToList();\n""",
    """            _traders = tasks.Select(t => t.Trader)\n                .Where(t => !string.IsNullOrEmpty(t))\n                .Distinct(StringComparer.OrdinalIgnoreCase)\n                .OrderBy(UiSortOrder.GetTraderRank)\n                .ThenBy(t => _loc.GetLocalizedTraderName(t), StringComparer.CurrentCulture)\n                .ToList();\n            _maps = tasks.Where(t => t.Maps != null)\n                .SelectMany(t => t.Maps!)\n                .Distinct(StringComparer.OrdinalIgnoreCase)\n                .OrderBy(UiSortOrder.GetMapRank)\n                .ThenBy(m => _loc.GetLocalizedMapName(m), StringComparer.CurrentCulture)\n                .ToList();\n""",
)
replace_once(
    "TarkovHelper/Pages/QuestListPage.xaml.cs",
    """                CompleteButtonVisibility = status is QuestStatus.Available or QuestStatus.Active\n                    ? Visibility.Visible : Visibility.Collapsed,\n                ActionButtonText = status == QuestStatus.Available ? \"시작\" : \"완료\",\n""",
    """                CompleteButtonVisibility = status == QuestStatus.Active\n                    ? Visibility.Visible : Visibility.Collapsed,\n""",
)
replace_once(
    "TarkovHelper/Pages/QuestListPage.xaml.cs",
    "                QuestStatus.Available => \"수주 가능\",\n",
    "                QuestStatus.Available => \"진행중\", // legacy value is normalized before display\n",
)
replace_once(
    "TarkovHelper/Pages/QuestListPage.xaml.cs",
    "                QuestStatus.Available => LevelLockedBrush,\n",
    "                QuestStatus.Available => ActiveBrush,\n",
)
replace_once(
    "TarkovHelper/Pages/QuestListPage.xaml.cs",
    """                var status = _progressService.GetStatus(vm.Task);\n                vm.Status = status;\n                vm.StatusText = GetStatusText(status, vm.Task);\n                vm.StatusBackground = GetStatusBrush(status);\n                vm.CompleteButtonVisibility = (status == QuestStatus.Active || status == QuestStatus.Locked || status == QuestStatus.LevelLocked)\n                    && status != QuestStatus.Unavailable ? Visibility.Visible : Visibility.Collapsed;\n""",
    """                var status = ActualQuestStatusService.Instance.CreateEvaluator().Evaluate(vm.Task);\n                vm.Status = status;\n                vm.StatusText = GetStatusText(status, vm.Task);\n                vm.StatusBackground = GetStatusBrush(status);\n                vm.CompleteButtonVisibility = status == QuestStatus.Active\n                    ? Visibility.Visible\n                    : Visibility.Collapsed;\n""",
)
replace_once(
    "TarkovHelper/Pages/QuestListPage.xaml.cs",
    """            BtnComplete.Visibility = status is QuestStatus.Available or QuestStatus.Active\n                ? Visibility.Visible : Visibility.Collapsed;\n            BtnComplete.Content = status == QuestStatus.Available ? \"시작\" : \"완료 처리\";\n""",
    """            BtnComplete.Visibility = status == QuestStatus.Active\n                ? Visibility.Visible : Visibility.Collapsed;\n            BtnComplete.Content = \"완료 처리\";\n""",
)
replace_once(
    "TarkovHelper/Pages/QuestListPage.xaml.cs",
    """            var status = ActualQuestStatusService.Instance.CreateEvaluator().Evaluate(vm.Task);\n            if (status == QuestStatus.Available)\n                _progressService.StartQuest(vm.Task);\n            else if (status == QuestStatus.Active)\n                _progressService.CompleteQuest(vm.Task, true);\n""",
    """            if (ActualQuestStatusService.Instance.CreateEvaluator().Evaluate(vm.Task) == QuestStatus.Active)\n                _progressService.CompleteQuest(vm.Task, true);\n""",
)
replace_once(
    "TarkovHelper/Pages/QuestListPage.xaml.cs",
    """            var status = ActualQuestStatusService.Instance.CreateEvaluator().Evaluate(selectedVm.Task);\n            if (status == QuestStatus.Available)\n                _progressService.StartQuest(selectedVm.Task);\n            else if (status == QuestStatus.Active)\n                _progressService.CompleteQuest(selectedVm.Task, true);\n""",
    """            if (ActualQuestStatusService.Instance.CreateEvaluator().Evaluate(selectedVm.Task) == QuestStatus.Active)\n                _progressService.CompleteQuest(selectedVm.Task, true);\n""",
)

replace_once(
    "TarkovHelper/Pages/QuestListPage.ActualQuestStatus.cs",
    """            viewModel.CompleteButtonVisibility =\n                status is QuestStatus.Available or QuestStatus.Active\n                    ? Visibility.Visible\n                    : Visibility.Collapsed;\n            viewModel.ActionButtonText = status == QuestStatus.Available ? \"시작\" : \"완료\";\n""",
    """            viewModel.CompleteButtonVisibility = status == QuestStatus.Active\n                ? Visibility.Visible\n                : Visibility.Collapsed;\n""",
)
regex_once(
    "TarkovHelper/Pages/QuestListPage.ActualQuestStatus.cs",
    r"\n    private void RemoveLegacyAvailableStatusFilter\(\)\n    \{.*?\n    \}\n\n    private void UpdateActualQuestStatistics",
    "\n    private void UpdateActualQuestStatistics",
)
replace_once(
    "TarkovHelper/Pages/QuestListPage.ActualQuestStatus.cs",
    "        var available = _allQuestViewModels.Count(vm => vm.Status == QuestStatus.Available);\n",
    "",
)
replace_once(
    "TarkovHelper/Pages/QuestListPage.ActualQuestStatus.cs",
    """            $\"진행 중: {active} | 수주 가능: {available} | 잠김: {locked} | \" +\n""",
    """            $\"진행 중: {active} | 잠김: {locked} | \" +\n""",
)

# Canonical Korean names and map ordering.
replace_once(
    "TarkovHelper/Services/LocalizationService.Core.cs",
    '        "prapor" => "프라포",\n',
    '        "prapor" => "프라퍼",\n',
)
replace_once(
    "TarkovHelper/Services/LocalizationService.Core.cs",
    '        "interchange" => "인터체인지",\n',
    '        "interchange" => "나들목",\n',
)
replace_once(
    "TarkovHelper/Services/LocalizationService.Core.cs",
    '        "the labyrinth" or "labyrinth" => "미궁",\n',
    '        "the labyrinth" or "labyrinth" => "미궁",\n        "icebreaker" or "ice breaker" or "icebreaker terminal" => "쇄빙선",\n',
)
replace_once(
    "TarkovHelper/Pages/Map/MapPage.xaml.cs",
    """        foreach (var mapKey in _trackerService.GetAllMapKeys())\n        {\n""",
    """        foreach (var mapKey in _trackerService.GetAllMapKeys()\n                     .OrderBy(UiSortOrder.GetMapRank)\n                     .ThenBy(key => _loc.GetLocalizedMapName(key), StringComparer.CurrentCulture))\n        {\n""",
)

# Ammo labels, fixed efficiency ordering, and dark-theme table styling.
replace_once(
    "TarkovHelper/Models/Ammo/AmmoItem.cs",
    '    public string DisplayText => $"{Effectiveness}x";\n',
    '    public string DisplayText => Effectiveness.ToString();\n',
)
replace_once(
    "TarkovHelper/Pages/AmmoPage.xaml.cs",
    """            .Select(group => new CaliberChoice(group.Key, group.First().CaliberDisplay, group.Count()))\n""",
    """            .Select(group => new CaliberChoice(group.Key, group.First().CaliberDisplay))\n""",
)
replace_once(
    "TarkovHelper/Pages/AmmoPage.xaml.cs",
    "        CaliberList.DisplayMemberPath = nameof(CaliberChoice.Label);\n",
    "        CaliberList.DisplayMemberPath = nameof(CaliberChoice.DisplayName);\n",
)
replace_once(
    "TarkovHelper/Pages/AmmoPage.xaml.cs",
    """                     .OrderBy(item => item.ArmorEfficiencyScore)\n                     .ThenBy(item => item.PenetrationPower)\n                     .ThenBy(item => item.NameKo, StringComparer.CurrentCulture))\n""",
    """                     .OrderBy(item => item.ArmorEfficiencyScore))\n""",
)
regex_once(
    "TarkovHelper/Pages/AmmoPage.xaml.cs",
    r"    private sealed record CaliberChoice\(string Key, string DisplayName, int Count\)\n    \{\n        public string Label => .*?\n    \}\n",
    """    private sealed record CaliberChoice(string Key, string DisplayName)\n    {\n        public override string ToString() => DisplayName;\n    }\n""",
)

replace_once(
    "TarkovHelper/Pages/AmmoPage.xaml",
    "             Unloaded=\"AmmoPage_Unloaded\">\n    <Grid Margin=\"20\">",
    """             Unloaded=\"AmmoPage_Unloaded\">\n    <UserControl.Resources>\n        <Style x:Key=\"AmmoToolbarToggleStyle\" TargetType=\"ToggleButton\">\n            <Setter Property=\"Background\" Value=\"{StaticResource BackgroundLightBrush}\"/>\n            <Setter Property=\"Foreground\" Value=\"{StaticResource TextPrimaryBrush}\"/>\n            <Setter Property=\"BorderBrush\" Value=\"{StaticResource BorderBrush}\"/>\n            <Setter Property=\"BorderThickness\" Value=\"1\"/>\n            <Setter Property=\"Padding\" Value=\"12,8\"/>\n            <Setter Property=\"Cursor\" Value=\"Hand\"/>\n            <Setter Property=\"FontFamily\" Value=\"{DynamicResource MaplestoryFont}\"/>\n            <Setter Property=\"FontSize\" Value=\"{DynamicResource BaseFontSize}\"/>\n            <Setter Property=\"Template\">\n                <Setter.Value>\n                    <ControlTemplate TargetType=\"ToggleButton\">\n                        <Border x:Name=\"border\"\n                                Background=\"{TemplateBinding Background}\"\n                                BorderBrush=\"{TemplateBinding BorderBrush}\"\n                                BorderThickness=\"{TemplateBinding BorderThickness}\"\n                                CornerRadius=\"4\" Padding=\"{TemplateBinding Padding}\">\n                            <ContentPresenter HorizontalAlignment=\"Center\" VerticalAlignment=\"Center\"/>\n                        </Border>\n                        <ControlTemplate.Triggers>\n                            <Trigger Property=\"IsMouseOver\" Value=\"True\">\n                                <Setter TargetName=\"border\" Property=\"Background\" Value=\"{StaticResource AccentBrush}\"/>\n                                <Setter TargetName=\"border\" Property=\"BorderBrush\" Value=\"{StaticResource AccentBrush}\"/>\n                            </Trigger>\n                            <Trigger Property=\"IsChecked\" Value=\"True\">\n                                <Setter TargetName=\"border\" Property=\"Background\" Value=\"{StaticResource AccentBrush}\"/>\n                                <Setter TargetName=\"border\" Property=\"BorderBrush\" Value=\"{StaticResource AccentBrush}\"/>\n                            </Trigger>\n                        </ControlTemplate.Triggers>\n                    </ControlTemplate>\n                </Setter.Value>\n            </Setter>\n        </Style>\n        <Style x:Key=\"AmmoGridCellStyle\" TargetType=\"DataGridCell\">\n            <Setter Property=\"Background\" Value=\"Transparent\"/>\n            <Setter Property=\"Foreground\" Value=\"{StaticResource TextPrimaryBrush}\"/>\n            <Setter Property=\"BorderBrush\" Value=\"{StaticResource BorderBrush}\"/>\n            <Setter Property=\"BorderThickness\" Value=\"0,0,0,1\"/>\n            <Setter Property=\"Padding\" Value=\"6,4\"/>\n            <Setter Property=\"FocusVisualStyle\" Value=\"{x:Null}\"/>\n        </Style>\n        <Style x:Key=\"AmmoGridHeaderStyle\" TargetType=\"DataGridColumnHeader\">\n            <Setter Property=\"Background\" Value=\"{StaticResource BackgroundMediumBrush}\"/>\n            <Setter Property=\"Foreground\" Value=\"{StaticResource TextPrimaryBrush}\"/>\n            <Setter Property=\"BorderBrush\" Value=\"{StaticResource BorderBrush}\"/>\n            <Setter Property=\"BorderThickness\" Value=\"0,0,1,1\"/>\n            <Setter Property=\"Padding\" Value=\"8,7\"/>\n            <Setter Property=\"HorizontalContentAlignment\" Value=\"Center\"/>\n        </Style>\n    </UserControl.Resources>\n    <Grid Margin=\"20\">""",
)
replace_once(
    "TarkovHelper/Pages/AmmoPage.xaml",
    "                          Content=\"표시 설정\"\n                          Padding=\"14,8\"",
    "                          Content=\"표시 설정\"\n                          Style=\"{StaticResource AmmoToolbarToggleStyle}\"\n                          Padding=\"14,8\"",
)
replace_once(
    "TarkovHelper/Pages/AmmoPage.xaml",
    """                  CanUserDeleteRows=\"False\"\n                  CanUserReorderColumns=\"True\"\n                  CanUserResizeColumns=\"True\"\n""",
    """                  CanUserDeleteRows=\"False\"\n                  CanUserSortColumns=\"False\"\n                  CanUserReorderColumns=\"False\"\n                  CanUserResizeColumns=\"True\"\n""",
)
replace_once(
    "TarkovHelper/Pages/AmmoPage.xaml",
    """                  Background=\"{StaticResource BackgroundDarkBrush}\"\n                  BorderBrush=\"{StaticResource BorderBrush}\"\n                  HorizontalGridLinesBrush=\"{StaticResource BorderBrush}\"\n""",
    """                  Background=\"{StaticResource BackgroundDarkBrush}\"\n                  RowBackground=\"{StaticResource BackgroundDarkBrush}\"\n                  AlternatingRowBackground=\"{StaticResource BackgroundMediumBrush}\"\n                  Foreground=\"{StaticResource TextPrimaryBrush}\"\n                  BorderBrush=\"{StaticResource BorderBrush}\"\n                  HorizontalGridLinesBrush=\"{StaticResource BorderBrush}\"\n                  CellStyle=\"{StaticResource AmmoGridCellStyle}\"\n                  ColumnHeaderStyle=\"{StaticResource AmmoGridHeaderStyle}\"\n""",
)

# Disable hover-at-edge auto scrolling in every ComboBox while preserving wheel,
# scrollbar and keyboard scrolling.
replace_once(
    "TarkovHelper/App.xaml.cs",
    "using System.Windows;\n",
    "using System.Windows;\nusing System.Windows.Controls;\nusing System.Windows.Input;\n",
)
replace_once(
    "TarkovHelper/App.xaml.cs",
    """            // DB 초기화를 가장 먼저 수행 (SettingsService, ProfileService 등이 안전하게 접근할 수 있도록 보장)\n            UserDataDbService.Instance.EnsureInitialized();\n""",
    """            // DB 초기화를 가장 먼저 수행 (SettingsService, ProfileService 등이 안전하게 접근할 수 있도록 보장)\n            UserDataDbService.Instance.EnsureInitialized();\n\n            EventManager.RegisterClassHandler(\n                typeof(ComboBoxItem),\n                FrameworkElement.RequestBringIntoViewEvent,\n                new RequestBringIntoViewEventHandler(ComboBoxItem_RequestBringIntoView));\n""",
)
replace_once(
    "TarkovHelper/App.xaml.cs",
    """        protected override void OnExit(ExitEventArgs e)\n""",
    """        private static void ComboBoxItem_RequestBringIntoView(\n            object sender,\n            RequestBringIntoViewEventArgs e)\n        {\n            if (sender is ComboBoxItem item &&\n                item.IsMouseOver &&\n                Mouse.LeftButton == MouseButtonState.Released &&\n                Mouse.MiddleButton == MouseButtonState.Released &&\n                Mouse.RightButton == MouseButtonState.Released)\n            {\n                e.Handled = true;\n            }\n        }\n\n        protected override void OnExit(ExitEventArgs e)\n""",
)

print("phase1 applied")
