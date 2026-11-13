using System.Reflection;
using System.Runtime.InteropServices;
using Mono.Addins;

[assembly: AssemblyTitle("OpenSim.Addons.DisplayName")]
[assembly: AssemblyDescription("")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("http://opensimulator.org")]
[assembly: AssemblyProduct("OpenSim.Addons.DisplayName")]
[assembly: AssemblyCopyright("Copyright (c) OpenSimulator.org Developers")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

[assembly: ComVisible(false)]

[assembly: Guid("2f0cf2c8-08fb-4fd8-bf9b-26b32574179b")]

[assembly: AssemblyVersion(OpenSim.VersionInfo.AssemblyVersionNumber)]

[assembly: Addin("OpenSim.DisplayName", OpenSim.VersionInfo.VersionNumber)]
[assembly: AddinDependency("OpenSim.Region.Framework", OpenSim.VersionInfo.VersionNumber)]
