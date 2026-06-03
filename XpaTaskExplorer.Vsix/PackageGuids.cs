using System;

namespace XpaTaskExplorer;

internal static class PackageGuids
{
    internal const string PackageGuidString = "3f9a7ac1-f0d4-4c3b-bb4e-f74c528c461e";
    internal const string CommandSetGuidString = "b22bc144-0da6-44f7-9a50-4d7c286b8a07";
    internal const string ToolWindowGuidString = "a66728d1-2e69-4325-86c8-e4fdb123d616";

    internal static readonly Guid CommandSet = new(CommandSetGuidString);
}
