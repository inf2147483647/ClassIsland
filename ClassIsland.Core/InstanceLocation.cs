using System.Diagnostics;

namespace ClassIsland.Core;

/// <summary>
/// 另一个正在运行的 ClassIsland 实例与本实例的位置关系。
/// </summary>
public enum InstanceLocationVerdict
{
    /// <summary>
    /// 存在与本实例共用程序目录或数据目录的实例，同时运行会争用同一份文件。
    /// </summary>
    SameLocation,

    /// <summary>
    /// 已确认正在运行的实例与本实例的程序目录和数据目录都不相同。
    /// </summary>
    DifferentLocation,

    /// <summary>
    /// 无法确认正在运行的实例的位置，应当按位置相同的保守策略处理。
    /// </summary>
    Unknown
}

/// <summary>
/// 解析 ClassIsland 实例的程序目录和数据目录，并判断另一个正在运行的实例是否与本实例冲突。
/// </summary>
/// <remarks>
/// <para>
/// 数据目录由打包类型决定：绿色版的数据位于程序目录下的 data 子目录，而安装版的数据位于用户目录，
/// 与安装位置无关。所以只有程序目录和数据目录都不同的两个实例才可以安全地同时运行。
/// </para>
/// <para>
/// 判断其它实例时只能拿到对方的可执行文件路径，因此这里按照与
/// <c>ClassIsland.App.ActivatePackageType</c>、<c>ClassIsland.App.ActivateAppDirectories</c>
/// 相同的规则推算对方的目录。本实例自身的目录一律使用应用启动时已经算好的值，不经过这里的推算。
/// 调整打包类型的判定逻辑时，请同步修改此处。
/// </para>
/// </remarks>
public static class InstanceLocation
{
    /// <summary>
    /// 未找到 PackageType 文件时使用的默认打包类型。
    /// </summary>
    public const string DefaultPackagingType = "folderClassic";

    /// <summary>
    /// 数据位于程序目录下 data 子目录的打包类型。
    /// </summary>
    public const string FolderPackagingType = "folder";

    /// <summary>
    /// MSIX 应用的安装目录名，用于识别没有 PackageType 文件的 MSIX 实例。
    /// </summary>
    private const string MsixInstallFolderName = "WindowsApps";

    /// <summary>
    /// 数据位于用户目录而不是程序目录的打包类型。这些实例即使安装在不同位置，也会共用同一份数据。
    /// </summary>
    public static readonly string[] UserDataPackagingTypes = ["installer", "deb", "appImage", "pkg", "msix", "apk"];

    /// <summary>
    /// 用户目录下的应用数据目录。
    /// </summary>
    public static string UserDataFolderPath => Path.GetFullPath(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClassIsland", "Data"));

    /// <summary>
    /// 按打包类型解析应用数据根目录。
    /// </summary>
    /// <param name="packagingType">打包类型。</param>
    /// <param name="packageRoot">打包根目录，绿色版据此定位 data 子目录。</param>
    /// <param name="executableDirectory">可执行文件所在目录，未识别的打包类型沿用程序目录。</param>
    /// <param name="isMacOs">是否运行在 macOS 上，macOS 下未识别的打包类型同样使用用户目录。</param>
    public static string ResolveAppRootFolderPath(string packagingType, string packageRoot,
        string executableDirectory, bool isMacOs)
        => Path.GetFullPath(packagingType switch
        {
            FolderPackagingType => Path.Combine(packageRoot, "data"),
            _ when UserDataPackagingTypes.Contains(packagingType) => UserDataFolderPath,
            _ => isMacOs ? UserDataFolderPath : executableDirectory
        });

    /// <summary>
    /// 读取指定可执行文件所属的打包类型，并给出对应的打包根目录。
    /// </summary>
    /// <param name="executablePath">可执行文件路径。</param>
    /// <param name="packageRoot">打包根目录。</param>
    public static string ResolvePackagingType(string executablePath, out string packageRoot)
    {
        var executableDirectory = Path.GetDirectoryName(Path.GetFullPath(executablePath)) ?? "./";
        var candidates = new[]
        {
            Path.Combine(executableDirectory, "PackageType"),
            Path.Combine(executableDirectory, "..", "PackageType")
        };

        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            try
            {
                var packagingType = File.ReadAllText(candidate).Replace(Environment.NewLine, "").Trim();
                packageRoot = Path.GetFullPath(Path.Combine(executableDirectory, ".."));
                return string.IsNullOrEmpty(packagingType) ? DefaultPackagingType : packagingType;
            }
            catch
            {
                // 读不到就继续尝试下一个候选位置。
            }
        }

        packageRoot = Path.GetFullPath(executableDirectory);

        // MSIX 版不会附带 PackageType 文件，但数据同样位于用户目录。
        // 按安装路径识别，避免被误判成数据位于程序目录的绿色版。
        return IsUnderMsixInstallFolder(executableDirectory) ? "msix" : DefaultPackagingType;
    }

    /// <summary>
    /// 判断另一个正在运行的实例是否与本实例冲突。
    /// </summary>
    /// <param name="currentExecutablePath">本实例的可执行文件路径。</param>
    /// <param name="currentAppRootFolderPath">本实例的数据根目录，取应用启动时算好的值。</param>
    public static InstanceLocationVerdict CompareRunningInstances(string currentExecutablePath,
        string currentAppRootFolderPath)
    {
        var currentExecutableDirectory = NormalizeDirectory(Path.GetDirectoryName(currentExecutablePath));
        var currentDataDirectory = NormalizeDirectory(currentAppRootFolderPath);

        var foundInstance = false;
        var undetermined = false;

        foreach (var process in GetOtherInstanceProcesses(currentExecutablePath))
        {
            using (process)
            {
                string otherExecutablePath;
                try
                {
                    otherExecutablePath = process.MainModule?.FileName ?? "";
                }
                catch
                {
                    // 对方可能以管理员身份运行，读不到路径就无法确认它的位置。
                    undetermined = true;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(otherExecutablePath))
                {
                    undetermined = true;
                    continue;
                }

                foundInstance = true;

                var otherExecutableDirectory = NormalizeDirectory(Path.GetDirectoryName(otherExecutablePath));
                var packagingType = ResolvePackagingType(otherExecutablePath, out var packageRoot);
                var otherDataDirectory = NormalizeDirectory(ResolveAppRootFolderPath(packagingType,
                    packageRoot, otherExecutableDirectory, OperatingSystem.IsMacOS()));

                if (IsSamePath(otherExecutableDirectory, currentExecutableDirectory) ||
                    IsSamePath(otherDataDirectory, currentDataDirectory))
                {
                    return InstanceLocationVerdict.SameLocation;
                }
            }
        }

        return foundInstance && !undetermined
            ? InstanceLocationVerdict.DifferentLocation
            : InstanceLocationVerdict.Unknown;
    }

    private static List<Process> GetOtherInstanceProcesses(string currentExecutablePath)
    {
        var result = new List<Process>();
        var processName = Path.GetFileNameWithoutExtension(currentExecutablePath);
        if (string.IsNullOrWhiteSpace(processName))
        {
            return result;
        }

        try
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                if (process.Id == Environment.ProcessId)
                {
                    process.Dispose();
                    continue;
                }

                result.Add(process);
            }
        }
        catch
        {
            // 枚举失败时返回空列表，由调用方按无法确认位置处理。
        }

        return result;
    }

    private static bool IsUnderMsixInstallFolder(string directory)
        => directory
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => string.Equals(segment, MsixInstallFolderName, StringComparison.OrdinalIgnoreCase));

    private static bool IsSamePath(string left, string right)
        => !string.IsNullOrEmpty(left) && !string.IsNullOrEmpty(right) &&
           string.Equals(left, right, PathComparison);

    private static string NormalizeDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "";
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch
        {
            return "";
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}
