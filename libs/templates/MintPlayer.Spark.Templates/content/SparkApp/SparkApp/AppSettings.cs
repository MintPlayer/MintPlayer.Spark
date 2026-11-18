using MintPlayer.Spark.Settings;

namespace SparkApp;

/// <summary>
/// Settings Administrators change at runtime, registered by AddSettings(typeof(AppSettings)). A value comes from the
/// stored setting, then configuration under its key, then the default here.
/// </summary>
public static class AppSettings
{
    public static readonly SparkSetting<int> PageSize = new("App:PageSize", 25, "Rows per page in the app's lists.");
}
