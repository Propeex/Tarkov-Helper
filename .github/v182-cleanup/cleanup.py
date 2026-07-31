from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    file = Path(path)
    text = file.read_text(encoding="utf-8")
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{path}: expected one match, found {count}")
    file.write_text(text.replace(old, new, 1), encoding="utf-8", newline="")


replace_once(
    "TarkovHelper.DatabaseSmoke/V182RequirementSmoke.cs",
    'AmmoDbService.GetCaliberDisplay("Caliber40mmRU")',
    'AmmoLocalization.GetCaliberDisplay("Caliber40mmRU")',
)

replace_once(
    "TarkovHelper/Pages/QuestListPage.xaml.cs",
    '                QuestStatus.Available => "진행중", // legacy value is normalized before display\n',
    "",
)
replace_once(
    "TarkovHelper/Pages/QuestListPage.xaml.cs",
    "                QuestStatus.Available => ActiveBrush,\n",
    "",
)

replace_once(
    "TarkovHelper/Services/QuestProgressService.cs",
    """                    case QuestStatus.Active:\n                        if (GetStatus(task) == QuestStatus.Available)\n                        {\n                            _questProgress[taskKey] = QuestStatus.Active;\n                            changedItems.Add((taskId ?? taskKey, task.NormalizedName, QuestStatus.Active));\n                        }\n                        break;\n""",
    """                    case QuestStatus.Active:\n                        // Eligible quests are already Active. Imported start events\n                        // require no separate persisted acceptance state.\n                        break;\n""",
)

print("v1.8.2 status cleanup applied")
