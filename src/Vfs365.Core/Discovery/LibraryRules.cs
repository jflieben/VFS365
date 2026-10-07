namespace Vfs365.Core.Discovery;

public enum LibraryAccess { ReadWrite, ReadOnly }

/// <summary>Static exclusions can't change, so they are cached and never looked up again.</summary>
public sealed record LibraryVerdict(bool Include, LibraryAccess Access, string? Reason, bool Static)
{
    internal static LibraryVerdict Excluded(string reason, bool isStatic) => new(false, LibraryAccess.ReadOnly, reason, isStatic);
}

/// <summary>Which discovered libraries to show. Language independent: internal names and flags, never titles.</summary>
public static class LibraryRules
{
    /// <summary>Bump when rules change: cached static exclusions from older rules are dropped.</summary>
    public const int Version = 2;

    /// <summary>Internal names (EntityTypeName) of system libraries, compared without spaces. These don't follow the site language.</summary>
    static readonly HashSet<string> SystemInternalNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "SiteAssets", "SitePages", "StyleLibrary", "FormServerTemplates", "PreservationHoldLibrary", "Pages", "PublishingImages",
        "SiteCollectionImages", "IWConvertedForms",
    };

    /// <summary>Template feature IDs from M365AutoLink.</summary>
    static readonly HashSet<string> ExcludedFeatureIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "00000000-0000-0000-0000-000000000000", "a0e5a010-1329-49d4-9e09-f280cdbed37d", "d11bc7d4-96c6-40e3-837d-3eb861805bfa",
        "00bfea71-c796-4402-9f2f-0eb9a6e71b18", "de12eebe-9114-4a4a-b7da-7585dc36a907",
    };

    public static LibraryVerdict Evaluate(ListMetadata list)
    {
        if (list.Hidden)
        {
            return LibraryVerdict.Excluded("Hidden library", true);
        }
        if (list.BaseTemplate is int template && template != 101)
        {
            return LibraryVerdict.Excluded($"Not a standard document library (BaseTemplate {template})", true);
        }
        if (list.IsCatalog || list.IsSystemList || (list.InternalName is { } name && SystemInternalNames.Contains(name.Replace(" ", ""))))
        {
            return LibraryVerdict.Excluded("System or catalog library", true);
        }
        if (list.TemplateFeatureId is { } feature && ExcludedFeatureIds.Contains(LibraryKey.Normalize(feature)))
        {
            return LibraryVerdict.Excluded($"Excluded template feature {feature}", true);
        }
        if (list.ExcludeFromOfflineClient)
        {
            return LibraryVerdict.Excluded("Site owner excluded it from offline clients", false);
        }
        if (list.ForceCheckout)
        {
            return new(true, LibraryAccess.ReadOnly, "Requires check-out", false);
        }
        return new(true, LibraryAccess.ReadWrite, null, false);
    }
}
