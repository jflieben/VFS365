namespace Vfs365.Core;

/// <summary>Identifies a driveItem. Items are tracked by this, never by path.</summary>
public readonly record struct ItemRef(string DriveId, string ItemId);
