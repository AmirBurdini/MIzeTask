namespace SupplierFeed.Api.Configuration;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    // Relative to the working directory. Override with the Database:Path setting or Database__Path.
    // The default lives here, not in appsettings.json: that file is only found when the app runs from
    // its project folder, so a copy there would be silently ignored elsewhere.
    public string Path { get; set; } = "data/supplierfeed.db";
}
