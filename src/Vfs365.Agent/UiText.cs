using System.Globalization;

namespace Vfs365.Agent;

/// <summary>
/// What users read in the tray menu, its tooltip, notifications and the Refresh hint, in English and Dutch. Dutch for a Dutch Windows
/// display language (CultureInfo.CurrentUICulture), English otherwise. Log lines and monitoring stay English.
/// </summary>
public static class UiText
{
    static readonly Dictionary<string, (string En, string Nl)> Table = new(StringComparer.Ordinal)
    {
        // Menu; & marks the access key
        ["Menu.Status"] = ("Status: {0}", "Status: {0}"),
        ["Menu.ShowFiles"] = ("&Show files", "&Bestanden weergeven"),
        ["Menu.ShowLog"] = ("Show &log", "&Logboek weergeven"),
        ["Menu.Refresh"] = ("&Refresh", "&Vernieuwen"),
        ["Menu.RefreshFrom"] = ("&Refresh (from {0})", "&Vernieuwen (vanaf {0})"),
        ["Menu.RefreshRunning"] = ("&Refresh (running)", "&Vernieuwen (bezig)"),
        ["Menu.Restart"] = ("Res&tart", "&Opnieuw starten"), // shown only once mounted, Retry only before: they never share T
        ["Menu.SignIn"] = ("S&ign in", "&Aanmelden"),
        ["Menu.Retry"] = ("Re&try", "Opnieuw &proberen"),
        ["Menu.Help"] = ("&Help", "&Help"),
        ["Menu.Version"] = ("VFS365 {0} by JSolve B.V.", "VFS365 {0} van JSolve B.V."),
        ["Tomorrow"] = ("tomorrow {0}", "morgen {0}"),

        // Status, also the tooltip ("VFS365: up to date")
        ["Status.Starting"] = ("starting", "wordt gestart"),
        ["Status.UpToDate"] = ("up to date", "bijgewerkt"),
        ["Status.Refreshing"] = ("refreshing", "vernieuwen"),
        ["Status.Waiting"] = ("{0} change(s) waiting to upload", "{0} wijziging(en) wachten op uploaden"),
        ["Status.SignIn"] = ("sign-in needed", "aanmelden nodig"),
        ["Status.Offline"] = ("offline", "offline"),
        ["Status.OfflineWaiting"] = ("offline, {0} change(s) waiting to upload", "offline, {0} wijziging(en) wachten op uploaden"),
        ["Status.Throttled"] = ("Microsoft 365 limits requests; background work waits", "Microsoft 365 beperkt verzoeken; achtergrondwerk wacht"),
        ["Status.WaitingNetwork"] = ("waiting for the network", "wacht op het netwerk"),
        ["Status.WaitingSignIn"] = ("waiting for sign-in", "wacht op aanmelding"),
        ["Status.Restarting"] = ("restarting after uploads finish", "start opnieuw zodra uploads klaar zijn"),
        ["Status.NotStarted"] = ("not started: {0}", "niet gestart: {0}"),
        ["Status.Taken"] = ("not started: another user on this PC has {0}", "niet gestart: een andere gebruiker op deze pc heeft {0}"),

        // Notifications
        ["Copy.Title"] = ("Saved as a copy", "Opgeslagen als kopie"),
        ["Copy.One"] = ("{0} was changed or locked in Microsoft 365. Your version is saved next to it as {1}.",
            "{0} is gewijzigd of vergrendeld in Microsoft 365. Uw versie staat ernaast als {1}."),
        ["Copy.Many"] = ("{0} files were changed or locked in Microsoft 365. Your versions are saved next to them as copies.",
            "{0} bestanden zijn gewijzigd of vergrendeld in Microsoft 365. Uw versies staan ernaast als kopie."),
        ["Waiting.Title"] = ("Waiting to upload", "Wacht op uploaden"),
        ["Waiting.One"] = ("{0} isn't uploaded yet: {1}. VFS365 keeps trying; your changes are kept on this PC.",
            "{0} is nog niet geüpload: {1}. VFS365 blijft het proberen; uw wijzigingen blijven op deze pc."),
        ["Waiting.Many"] = ("{0} files are waiting to upload: {1}. VFS365 keeps trying; your changes are kept on this PC.",
            "{0} bestanden wachten op uploaden: {1}. VFS365 blijft het proberen; uw wijzigingen blijven op deze pc."),
        ["NotDeleted.Title"] = ("Not deleted", "Niet verwijderd"),
        ["NotDeleted.One"] = ("{0} could not be deleted in Microsoft 365 ({1}). It is back in its folder.",
            "{0} kon niet worden verwijderd in Microsoft 365 ({1}). Het staat weer in de map."),
        ["NotDeleted.Many"] = ("{0} files could not be deleted in Microsoft 365. They are back in their folders.",
            "{0} bestanden konden niet worden verwijderd in Microsoft 365. Ze staan weer in hun mappen."),
        ["Uploaded.Title"] = ("Uploaded", "Geüpload"),
        ["Uploaded.One"] = ("{0} is saved to Microsoft 365.", "{0} is opgeslagen in Microsoft 365."),
        ["Uploaded.Many"] = ("{0} waiting files are saved to Microsoft 365.", "{0} wachtende bestanden zijn opgeslagen in Microsoft 365."),
        ["Refreshed.Title"] = ("Refreshed", "Vernieuwd"),
        ["Refreshed.Folders"] = ("Folders update as you open them.", "Mappen worden bijgewerkt zodra u ze opent."),
        ["Refreshed.Libraries"] = ("{0} libraries found. Folders update as you open them.", "{0} bibliotheken gevonden. Mappen worden bijgewerkt zodra u ze opent."),
        ["RefreshFailed.Title"] = ("Refresh failed", "Vernieuwen mislukt"),
        ["RefreshFailed.Text"] = ("Refresh failed: {0}. The libraries you had stay; you can try again.",
            "Vernieuwen mislukt: {0}. Uw bibliotheken blijven staan; u kunt het opnieuw proberen."),
        ["SignIn.Title"] = ("Sign-in needed", "Aanmelden nodig"),
        ["SignIn.Text"] = ("VFS365 couldn't sign you in by itself: {0}. Click here, or choose Sign in in the VFS365 menu.",
            "VFS365 kon u niet zelf aanmelden: {0}. Klik hier, of kies Aanmelden in het VFS365-menu."),
        ["Recovered.Title"] = ("Uploading earlier changes", "Eerdere wijzigingen uploaden"),
        ["Recovered.Text"] = ("{0} file(s) changed before VFS365 last stopped are being uploaded.",
            "{0} bestand(en) die zijn gewijzigd voordat VFS365 stopte, worden geüpload."),
        ["Policy.Title"] = ("Settings changed", "Instellingen gewijzigd"),
        ["Policy.Text"] = ("Your organisation changed VFS365's settings ({0}). They apply after Restart in the VFS365 menu, or at the next sign-in.",
            "Uw organisatie heeft instellingen van VFS365 gewijzigd ({0}). Ze gelden na Opnieuw starten in het VFS365-menu, of bij de volgende aanmelding."),
        ["Letter.Title"] = ("Drive letter not available", "Stationsletter niet beschikbaar"),
        ["Letter.Text"] = ("{0} is in use or not valid, so your files are in the navigation pane and at {1}, without a drive letter.",
            "{0} is in gebruik of ongeldig. Uw bestanden staan daarom zonder stationsletter in het navigatiedeelvenster en op {1}."),
        ["Security.Title"] = ("Security warning", "Beveiligingswaarschuwing"),
        ["Security.Text"] = ("Another user on this PC has taken {0}. VFS365 didn't start your drive, so nothing can be saved there by mistake. Tell your IT department.",
            "Een andere gebruiker op deze pc heeft {0} in gebruik. VFS365 heeft uw station niet gestart, zodat daar niets per ongeluk wordt opgeslagen. Meld dit bij uw IT-afdeling."),
        ["Failed.Title"] = ("VFS365 didn't start", "VFS365 is niet gestart"),
        ["Failed.Text"] = ("{0}. Choose Retry in the VFS365 menu, or Show log for the details.",
            "{0}. Kies Opnieuw proberen in het VFS365-menu, of Logboek weergeven voor de details."),
        ["Shutdown.Uploading"] = ("Uploading {0} changed file(s) to Microsoft 365", "{0} gewijzigd(e) bestand(en) uploaden naar Microsoft 365"),

        // Plain causes (PlainCause)
        ["Cause.Offline"] = ("offline", "offline"),
        ["Cause.Throttled"] = ("Microsoft 365 limits requests for now", "Microsoft 365 beperkt tijdelijk het aantal verzoeken"),
        ["Cause.SignedOut"] = ("sign-in needed", "aanmelden nodig"),
        ["Cause.Locked"] = ("the file is locked or checked out", "het bestand is vergrendeld of uitgecheckt"),
        ["Cause.NoAccess"] = ("no access", "geen toegang"),
        ["Cause.ReadOnly"] = ("the library is read-only for now", "de bibliotheek is tijdelijk alleen-lezen"),
        ["Cause.Unavailable"] = ("Microsoft 365 isn't responding properly", "Microsoft 365 reageert niet goed"),
        ["Cause.Gone"] = ("deleted or moved in Microsoft 365", "verwijderd of verplaatst in Microsoft 365"),
        ["Cause.Other"] = ("an unexpected error", "een onverwachte fout"),

        // Sign-in problems in plain words (SignInText)
        ["Aad.Mfa"] = ("multi-factor authentication is needed", "meervoudige verificatie is nodig"),
        ["Aad.Expired"] = ("your sign-in expired, for example after a password change", "uw aanmelding is verlopen, bijvoorbeeld na een wachtwoordwijziging"),
        ["Aad.Blocked"] = ("your organisation's sign-in rules block it (Conditional Access)", "de aanmeldregels van uw organisatie blokkeren het (voorwaardelijke toegang)"),
        ["Aad.Consent"] = ("an administrator must approve VFS365 for your organisation first", "een beheerder moet VFS365 eerst goedkeuren voor uw organisatie"),
        ["Aad.Disabled"] = ("your account is disabled", "uw account is uitgeschakeld"),
        ["Aad.Guest"] = ("this account can't be used with VFS365 (a guest or an account from another organisation)",
            "dit account werkt niet met VFS365 (een gast of een account van een andere organisatie)"),
        ["Aad.Cancelled"] = ("sign-in was cancelled", "aanmelden is geannuleerd"),
        ["Aad.Other"] = ("sign-in failed", "aanmelden is mislukt"),

        // Refresh hint (RefreshRule)
        ["Refresh.Running"] = ("Refreshing now.", "Wordt nu vernieuwd."),
        ["Refresh.Daily"] = ("Refresh works once a day. You refreshed at {0}{1}; you can again from {2}.",
            "Vernieuwen kan één keer per dag. U hebt vernieuwd om {0}{1}; u kunt het weer vanaf {2}."),
        ["Refresh.Yesterday"] = (" yesterday", " gisteren"),
        ["Refresh.Recent"] = ("Your libraries were read {0} minute(s) ago. Refresh works when that is more than an hour ago: from {1}.",
            "Uw bibliotheken zijn {0} minuut/minuten geleden gelezen. Vernieuwen kan als dat meer dan een uur geleden is: vanaf {1}."),
    };

    public static IReadOnlyCollection<string> Keys => Table.Keys;

    public static bool IsDutch(CultureInfo culture) => culture.TwoLetterISOLanguageName == "nl";

    /// <summary>The text for <paramref name="key"/> in <paramref name="culture"/> (default: the display language), formatted with <paramref name="args"/>.</summary>
    public static string Get(CultureInfo? culture, string key, params object?[] args)
    {
        culture ??= CultureInfo.CurrentUICulture;
        var (en, nl) = Table[key];
        var text = IsDutch(culture) ? nl : en;
        return args.Length == 0 ? text : string.Format(culture, text, args);
    }

    public static string Get(string key, params object?[] args) => Get(null, key, args);
}
