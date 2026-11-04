using System.Reflection;
using System.Runtime.Versioning;
using ClassIsland;

#if NIX
[assembly: AssemblyVersion("0.0.0.0")]
[assembly: AssemblyInformationalVersion("NIXBUILD+NIXBUILD_LONG_HASH")]
#else
// 程序集版本固定为 2.1.2.0。插件是按发布版本（ClassIsland.Core 2.1.2.0）编译的，
// 宿主程序集版本必须与插件引用中的版本一致，否则插件会因
// “Could not load file or assembly 'ClassIsland.Core, Version=2.1.2.0'” 而无法加载。
// 发布其它版本时请同步修改这里。
[assembly: AssemblyVersion("2.1.2.0")]
[assembly: AssemblyInformationalVersion($"2.1.2.0+{GitInfo.CommitHash}")]
#endif

[assembly: AssemblyTitle("ClassIsland")]
[assembly: AssemblyProduct("ClassIsland")]
#if NETCOREAPP
// [assembly: SupportedOSPlatform("Windows")]
#endif
#if Platforms_MacOs
[assembly:SupportedOSPlatform("macos")]
#endif
 
