using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClassIsland.Core;
using ClassIsland.Core.Models.Automation;
using ClassIsland.Core.Models.Components;
using ClassIsland.Services;
using ClassIsland.Shared.Helpers;
using ClassIsland.Shared.Models.Profile;
using Microsoft.Extensions.Logging;

namespace ClassIsland.Helpers.ProfileTransferHelpers;

/// <summary>
/// 将当前版本的数据转换为 ClassIsland 1.7.0.x 可读取的格式。
/// 无法在 1.7.0.x 中表达的数据条目会被静默跳过。
/// </summary>
public static class ClassIslandV1DataExportHelper
{
    private const string V1WeatherIconMaterialDesign = "classisland.weatherIcons.materialDesign";
    private const string V1WeatherIconSimpleText = "classisland.weatherIcons.simpleText";

    private static readonly DateTime V1TimeBaseDate = new(2024, 5, 25);

    private static readonly JsonSerializerOptions ExportJsonOptions = new()
    {
        WriteIndented = true
    };

    /// <summary>
    /// 可以在 1.7.0.x 中直接复用的配置目录。
    /// </summary>
    private static readonly string[] CompatibleConfigDirectories =
    [
        "ExternalAudios",
        "GptSoVitsSettingsPresets",
    ];

    /// <summary>
    /// 1.7.0.x 中已注册的组件。
    /// </summary>
    private static readonly HashSet<string> V1ComponentIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "9E1AF71D-8F77-4B21-A342-448787104DD9", // 时钟
        "7C645D35-8151-48BA-B4AC-15017460D994", // 倒计时
        "DF3F8295-21F6-482E-BADA-FA0E5F14BB66", // 日期
        "C911D762-107F-40C6-84CC-0146AB3C86B1", // 分组容器
        "70FCD5EA-3FAE-4E06-ACA2-4F4DF47F9ACD", // 滚动容器
        "1DB2017D-E374-4BC6-9D57-0B4ADF03A6B8", // 课程表
        "AB0F26D5-9DF6-4575-B844-73B04D0907C1", // 分割线
        "7E19A113-D281-4F33-970A-834A0B78B5AD", // 轮播容器
        "EE8F66BD-C423-4E7C-AB46-AA9976B00E08", // 文本
        "CA495086-E297-4BEB-9603-C5C1C1A8551E", // 天气简报
    };

    /// <summary>
    /// 1.7.0.x 中已注册的行动。
    /// </summary>
    private static readonly HashSet<string> V1ActionIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "classisland.broadcastSignal",
        "classisland.settings.currentComponentConfig",
        "classisland.settings.theme",
        "classisland.settings.windowDockingLocation",
        "classisland.settings.windowLayer",
        "classisland.settings.windowDockingOffsetX",
        "classisland.settings.windowDockingOffsetY",
        "classisland.os.run",
        "classisland.showNotification",
        "classisland.action.sleep",
        "classisland.notification.weather",
        "classisland.app.quit",
        "classisland.app.restart",
    };

    /// <summary>
    /// 1.7.0.x 中已注册的触发器。
    /// </summary>
    private static readonly HashSet<string> V1TriggerIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "classisland.lifetime.startup",
        "classisland.lifetime.stopping",
        "classisland.cron",
        "classisland.lessons.currentTimeStateChanged",
        "classisland.lessons.onAfterSchool",
        "classisland.lessons.onBreakingTime",
        "classisland.lessons.onClass",
        "classisland.lessons.preTimePoint",
        "classisland.ruleSet.rulesetChanged",
        "classisland.signal",
        "classisland.uri",
    };

    /// <summary>
    /// 将当前数据导出为 ClassIsland 1.7.0.x 的目录结构。
    /// </summary>
    /// <param name="targetDirectory">导出目标目录。</param>
    /// <param name="exportSettings">是否导出应用设置。</param>
    /// <param name="exportProfiles">是否导出课表配置。</param>
    /// <param name="exportOtherConfig">是否导出其它配置。</param>
    /// <param name="logger">日志记录器。</param>
    public static void Export(string targetDirectory, bool exportSettings, bool exportProfiles,
        bool exportOtherConfig, ILogger? logger = null)
    {
        Directory.CreateDirectory(targetDirectory);

        if (exportSettings)
        {
            ExportSettings(targetDirectory, logger);
        }

        if (exportProfiles)
        {
            ExportProfiles(targetDirectory, logger);
        }

        if (exportOtherConfig)
        {
            ExportOtherConfig(targetDirectory, logger);
        }
    }

    private static void ExportSettings(string targetDirectory, ILogger? logger)
    {
        var sourcePath = Path.Combine(CommonDirectories.AppRootFolderPath, "Settings.json");
        if (!File.Exists(sourcePath))
        {
            return;
        }

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(sourcePath)) as JsonObject ?? new JsonObject();
            var weatherIconId = TryGetString(root, "WeatherIconId");
            if (!string.IsNullOrWhiteSpace(weatherIconId) && weatherIconId != V1WeatherIconSimpleText)
            {
                root["WeatherIconId"] = V1WeatherIconMaterialDesign;
            }

            File.WriteAllText(Path.Combine(targetDirectory, "Settings.json"),
                root.ToJsonString(ExportJsonOptions));
        }
        catch (Exception exception)
        {
            logger?.LogWarning(exception, "跳过无法导出到 ClassIsland 1.7.0.x 的应用设置");
        }
    }

    private static void ExportProfiles(string targetDirectory, ILogger? logger)
    {
        var destinationDirectory = Path.Combine(targetDirectory, "Profiles");
        Directory.CreateDirectory(destinationDirectory);

        if (!Directory.Exists(ProfileService.ProfilePath))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(ProfileService.ProfilePath, "*.json"))
        {
            try
            {
                var profile = ConfigureFileHelper.LoadConfigUnWrapped<Profile>(path, false);
                var node = ConvertProfile(profile);
                File.WriteAllText(Path.Combine(destinationDirectory, Path.GetFileName(path)),
                    node.ToJsonString(ExportJsonOptions));
            }
            catch (Exception exception)
            {
                logger?.LogWarning(exception, "跳过无法导出到 ClassIsland 1.7.0.x 的档案：{Path}", path);
            }
        }
    }

    private static void ExportOtherConfig(string targetDirectory, ILogger? logger)
    {
        var configDirectory = Path.Combine(targetDirectory, "Config");
        Directory.CreateDirectory(configDirectory);

        ExportComponents(Path.Combine(configDirectory, "Islands"), logger);
        ExportAutomations(Path.Combine(configDirectory, "Automations"), logger);

        // 1.7.0.x 与 2.x 的主题、插件索引、管理策略等配置格式不兼容，只复制可直接复用的资源文件。
        foreach (var directoryName in CompatibleConfigDirectories)
        {
            var sourceDirectory = Path.Combine(CommonDirectories.AppConfigPath, directoryName);
            if (Directory.Exists(sourceDirectory))
            {
                FileFolderService.CopyFolder(sourceDirectory,
                    Path.Combine(configDirectory, directoryName), true);
            }
        }
    }

    private static void ExportComponents(string destinationDirectory, ILogger? logger)
    {
        Directory.CreateDirectory(destinationDirectory);

        if (!Directory.Exists(ComponentsService.ComponentSettingsPath))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(ComponentsService.ComponentSettingsPath, "*.json"))
        {
            try
            {
                var profile = ConfigureFileHelper.LoadConfigUnWrapped<ComponentProfile>(path, false);
                var array = ConvertComponentProfile(profile);
                File.WriteAllText(Path.Combine(destinationDirectory, Path.GetFileName(path)),
                    array.ToJsonString(ExportJsonOptions));
            }
            catch (Exception exception)
            {
                logger?.LogWarning(exception, "跳过无法导出到 ClassIsland 1.7.0.x 的组件配置：{Path}", path);
            }
        }
    }

    private static void ExportAutomations(string destinationDirectory, ILogger? logger)
    {
        Directory.CreateDirectory(destinationDirectory);

        if (!Directory.Exists(AutomationService.AutomationConfigsFolderPath))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(AutomationService.AutomationConfigsFolderPath, "*.json"))
        {
            try
            {
                var workflows =
                    ConfigureFileHelper.LoadConfigUnWrapped<ObservableCollection<Workflow>>(path, false);
                var compatibleWorkflows = new ObservableCollection<Workflow>(workflows.Where(IsWorkflowCompatible));
                ConfigureFileHelper.SaveConfig(Path.Combine(destinationDirectory, Path.GetFileName(path)),
                    compatibleWorkflows, true);
            }
            catch (Exception exception)
            {
                logger?.LogWarning(exception, "跳过无法导出到 ClassIsland 1.7.0.x 的自动化配置：{Path}", path);
            }
        }
    }

    private static JsonObject ConvertProfile(Profile profile)
    {
        var root = ConfigureFileHelper.SerializeToNode(profile) as JsonObject ?? new JsonObject();
        // 日程模式、档案迁移记录等均为 2.x 特有数据。
        root.Remove(nameof(Profile.Migrations));
        root.Remove("ScheduleItems");
        root.Remove("ScheduleType");
        root.Remove("OrderedSchedulesV2");

        var timeLayouts = root["TimeLayouts"] as JsonObject;
        if (timeLayouts != null)
        {
            foreach (var timeLayout in timeLayouts)
            {
                ConvertTimeLayout(timeLayout.Value as JsonObject);
            }
        }

        if (root["ClassPlans"] is not JsonObject classPlans)
        {
            return root;
        }

        foreach (var key in classPlans.Select(x => x.Key).ToList())
        {
            if (classPlans[key] is not JsonObject classPlan || !IsClassPlanCompatible(classPlan, timeLayouts))
            {
                classPlans.Remove(key);
            }
        }

        RemoveMissingClassPlanReferences(root, classPlans);
        return root;
    }

    private static void ConvertTimeLayout(JsonObject? timeLayout)
    {
        if (timeLayout?["Layouts"] is not JsonArray layouts)
        {
            return;
        }

        foreach (var item in layouts.OfType<JsonObject>())
        {
            item["StartSecond"] = FormatV1DateTime(ParseTimeSpan(item["StartTime"]));
            item["EndSecond"] = FormatV1DateTime(ParseTimeSpan(item["EndTime"]));
            item.Remove("StartTime");
            item.Remove("EndTime");
            FilterUnsupportedActions(item["ActionSet"] as JsonObject);
        }
    }

    private static bool IsClassPlanCompatible(JsonObject classPlan, JsonObject? timeLayouts)
    {
        var timeLayoutId = TryGetString(classPlan, "TimeLayoutId");
        if (string.IsNullOrWhiteSpace(timeLayoutId) || timeLayouts == null ||
            !timeLayouts.ContainsKey(timeLayoutId))
        {
            return false;
        }

        if (classPlan["TimeRule"] is not JsonObject timeRule)
        {
            return true;
        }

        // 1.7.0.x 只支持每周规则，按日期、循环启用或限制启用日期范围的课表无法正确表达。
        if (timeRule["Type"] is JsonValue typeValue && typeValue.TryGetValue<int>(out var type) && type != 0)
        {
            return false;
        }

        return timeRule["RestrictsEnableRange"] is not JsonValue restrictValue ||
               !restrictValue.TryGetValue<bool>(out var restrict) || !restrict;
    }

    private static void RemoveMissingClassPlanReferences(JsonObject root, JsonObject classPlans)
    {
        if (root["OrderedSchedules"] is JsonObject orderedSchedules)
        {
            foreach (var key in orderedSchedules.Select(x => x.Key).ToList())
            {
                var classPlanId = TryGetString(orderedSchedules[key] as JsonObject, "ClassPlanId");
                if (string.IsNullOrWhiteSpace(classPlanId) || !classPlans.ContainsKey(classPlanId))
                {
                    orderedSchedules.Remove(key);
                }
            }
        }

        RemoveMissingClassPlanReference(root, "TempClassPlanId", classPlans);
        RemoveMissingClassPlanReference(root, "OverlayClassPlanId", classPlans);
    }

    private static void RemoveMissingClassPlanReference(JsonObject root, string propertyName, JsonObject classPlans)
    {
        var classPlanId = TryGetString(root, propertyName);
        if (!string.IsNullOrWhiteSpace(classPlanId) && !classPlans.ContainsKey(classPlanId))
        {
            root[propertyName] = null;
        }
    }

    private static void FilterUnsupportedActions(JsonObject? actionSet)
    {
        if (actionSet?["Actions"] is not JsonArray actions)
        {
            return;
        }

        for (var i = actions.Count - 1; i >= 0; i--)
        {
            var actionId = TryGetString(actions[i] as JsonObject, "Id");
            if (string.IsNullOrWhiteSpace(actionId) || !V1ActionIds.Contains(actionId))
            {
                actions.RemoveAt(i);
            }
        }
    }

    private static JsonArray ConvertComponentProfile(ComponentProfile profile)
    {
        var result = new JsonArray();
        var lines = profile.Lines
            .Select((line, index) => (line, index))
            .OrderByDescending(x => x.line.IsMainLine)
            .ThenBy(x => x.index)
            .Select(x => x.line);

        var lineNumber = 0;
        foreach (var line in lines)
        {
            if (ConfigureFileHelper.SerializeToNode(line) is JsonObject lineNode &&
                lineNode["Children"] is JsonArray children)
            {
                foreach (var child in children.ToList())
                {
                    if (child is not JsonObject childObject || !IsSupportedComponent(childObject))
                    {
                        continue;
                    }

                    FilterUnsupportedChildren(childObject);
                    childObject["RelativeLineNumber"] = lineNumber;
                    result.Add(childObject.DeepClone());
                }
            }

            lineNumber++;
        }

        return result;
    }

    private static bool IsSupportedComponent(JsonObject component)
    {
        var componentId = TryGetString(component, "Id");
        return !string.IsNullOrWhiteSpace(componentId) && V1ComponentIds.Contains(componentId);
    }

    private static void FilterUnsupportedChildren(JsonObject component)
    {
        if (component["Settings"] is not JsonObject settings || settings["Children"] is not JsonArray children)
        {
            return;
        }

        for (var i = children.Count - 1; i >= 0; i--)
        {
            if (children[i] is JsonObject child && IsSupportedComponent(child))
            {
                FilterUnsupportedChildren(child);
                continue;
            }

            children.RemoveAt(i);
        }
    }

    private static bool IsWorkflowCompatible(Workflow workflow)
    {
        var triggersCompatible = (workflow.Triggers ?? []).All(x =>
            !string.IsNullOrWhiteSpace(x.Id) && V1TriggerIds.Contains(x.Id));
        var actionsCompatible = (workflow.ActionSet?.ActionItems ?? []).All(x =>
            !string.IsNullOrWhiteSpace(x.Id) && V1ActionIds.Contains(x.Id));
        return triggersCompatible && actionsCompatible;
    }

    private static string? TryGetString(JsonObject? jsonObject, string propertyName)
    {
        return jsonObject?[propertyName] is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : null;
    }

    private static TimeSpan ParseTimeSpan(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue<string>(out var text) &&
               TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var time)
            ? time
            : TimeSpan.Zero;
    }

    private static string FormatV1DateTime(TimeSpan time)
    {
        return V1TimeBaseDate.Add(time).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
    }
}
