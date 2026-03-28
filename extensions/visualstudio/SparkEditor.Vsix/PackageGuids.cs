using System;

namespace SparkEditor.Vsix
{
    internal static class PackageGuids
    {
        public const string SparkEditorPackageString = "b7e1a2c3-d4f5-6789-abcd-ef0123456789";
        public static readonly Guid SparkEditorPackage = new Guid(SparkEditorPackageString);

        public const string CommandSetString = "c8f2b3d4-e5a6-7890-bcde-f01234567890";
        public static readonly Guid CommandSet = new Guid(CommandSetString);
    }

    internal static class PackageIds
    {
        public const int OpenSparkEditorCommand = 0x0100;
    }
}
