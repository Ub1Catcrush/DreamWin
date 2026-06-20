using System.Runtime.Versioning;

// This app is WPF (net8.0-windows) and only ever runs on Windows. Declaring this here
// lets the compiler suppress CA1416 for Windows-only APIs (Registry, ProtectedData,
// WindowsIdentity, FileSystemAclExtensions, etc.) used throughout the project, since
// GenerateAssemblyInfo is disabled and the SDK can't emit this attribute automatically.
[assembly: SupportedOSPlatform("windows")]
