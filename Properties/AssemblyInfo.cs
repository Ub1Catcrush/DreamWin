using System.Runtime.Versioning;

// This app is WPF (net8.0-windows) and only ever runs on Windows. Declaring this here
// lets the compiler suppress CA1416 for Windows-only APIs (Registry, ProtectedData,
// WindowsIdentity, FileSystemAclExtensions, etc.) used throughout the project. The SDK's
// auto-generated assembly info (GenerateAssemblyInfo, now enabled) does not emit this
// attribute on its own, so it still needs to be declared explicitly here.
[assembly: SupportedOSPlatform("windows")]

// NOTE: AssemblyVersion / AssemblyFileVersion / AssemblyInformationalVersion are
// intentionally NOT declared here anymore. They used to be hardcoded ("1.0.0.0"),
// which silently overrode whatever version CI passed in via /p:Version, so the
// running app always reported "1.0.0" no matter what version.properties said.
// DreamWin.csproj now reads version.properties directly and emits these attributes
// itself via <Version>/<AssemblyVersion>/<FileVersion>/<InformationalVersion>,
// so there is a single source of truth and no risk of drift.
