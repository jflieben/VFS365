using System.Globalization;

namespace Vfs365.Agent;

/// <summary>
/// What users read in the tray menu, its tooltip and notifications, in English and Dutch. Dutch for a Dutch Windows
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
        ["Menu.Diagnostics"] = ("Collect &diagnostics", "&Diagnosegegevens verzamelen"),
        ["Menu.Restart"] = ("Res&tart", "&Opnieuw starten"), // shown only once mounted, Retry only before: they never share T
        ["Menu.SignIn"] = ("S&ign in", "&Aanmelden"),
        ["Menu.Retry"] = ("Re&try", "Opnieuw &proberen"),
        ["Menu.Help"] = ("&Help", "&Help"),
        ["Menu.Version"] = ("VFS365 {0} by JSolve B.V.", "VFS365 {0} van JSolve B.V."),

        // Status, also the tooltip ("VFS365: up to date")
        ["Status.Starting"] = ("starting", "wordt gestart"),
        ["Status.UpToDate"] = ("up to date", "bijgewerkt"),
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
        ["Status.Stopped"] = ("stopped after unexpected errors", "gestopt na onverwachte fouten"),

        // Why a start waits, where only the exception text was known (StartText)
        ["Start.Mounted"] = ("your drive is already running, in another session or program", "uw station draait al, in een andere sessie of een ander programma"),
        ["Start.WinFsp"] = ("Windows could not start the drive; WinFsp may be missing", "Windows kon het station niet starten; mogelijk ontbreekt WinFsp"),
        ["Start.Files"] = ("VFS365 can't read or write its files in your profile", "VFS365 kan zijn bestanden in uw profiel niet lezen of schrijven"),

        // Notifications
        ["Copy.Title"] = ("Saved as a copy", "Opgeslagen als kopie"),
        ["Copy.One"] = ("{0} was changed or locked in Microsoft 365. Your version is saved next to it as {1}.",
            "{0} is gewijzigd of vergrendeld in Microsoft 365. Uw versie staat ernaast als {1}."),
        ["Copy.Many"] = ("{0} files were changed or locked in Microsoft 365. Your versions are saved next to them as copies.",
            "{0} bestanden zijn gewijzigd of vergrendeld in Microsoft 365. Uw versies staan ernaast als kopie."),
        ["Copy.By"] = ("{2} saved {0} in Microsoft 365 ({3}). Your version is saved next to it as {1}.",
            "{2} heeft {0} opgeslagen in Microsoft 365 ({3}). Uw versie staat ernaast als {1}."),
        ["Copy.Folder"] = ("The folder of {0} was moved or deleted in Microsoft 365. Your version is saved as {1}.",
            "De map van {0} is verplaatst of verwijderd in Microsoft 365. Uw versie is opgeslagen als {1}."),
        ["Waiting.Title"] = ("Waiting to upload", "Wacht op uploaden"),
        ["Waiting.One"] = ("{0} isn't uploaded yet: {1}. VFS365 keeps trying; your changes are kept on this PC.",
            "{0} is nog niet geüpload: {1}. VFS365 blijft het proberen; uw wijzigingen blijven op deze pc."),
        ["Waiting.Many"] = ("{0} files are waiting to upload: {1}. VFS365 keeps trying; your changes are kept on this PC.",
            "{0} bestanden wachten op uploaden: {1}. VFS365 blijft het proberen; uw wijzigingen blijven op deze pc."),
        ["Waiting.Kept.One"] = ("{0} isn't uploaded: {1}. It is kept on this PC until you save, rename or move it again.",
            "{0} is niet geüpload: {1}. Het blijft op deze pc tot u het opnieuw opslaat, hernoemt of verplaatst."),
        ["Waiting.Kept.Many"] = ("{0} files aren't uploaded: {1}. They are kept on this PC until you save, rename or move them again.",
            "{0} bestanden zijn niet geüpload: {1}. Ze blijven op deze pc tot u ze opnieuw opslaat, hernoemt of verplaatst."),
        ["NotDeleted.Title"] = ("Not deleted", "Niet verwijderd"),
        ["NotDeleted.One"] = ("{0} could not be deleted in Microsoft 365: {1}. It is back in its folder.",
            "{0} kon niet worden verwijderd in Microsoft 365: {1}. Het staat weer in de map."),
        ["NotDeleted.Many"] = ("{0} files could not be deleted in Microsoft 365. They are back in their folders.",
            "{0} bestanden konden niet worden verwijderd in Microsoft 365. Ze staan weer in hun mappen."),
        ["Uploaded.Title"] = ("Uploaded", "Geüpload"),
        ["Uploaded.One"] = ("{0} is saved to Microsoft 365.", "{0} is opgeslagen in Microsoft 365."),
        ["Uploaded.Many"] = ("{0} waiting files are saved to Microsoft 365.", "{0} wachtende bestanden zijn opgeslagen in Microsoft 365."),
        ["SignIn.Title"] = ("Sign-in needed", "Aanmelden nodig"),
        ["SignIn.Text"] = ("VFS365 couldn't sign you in by itself: {0}. Click here, or choose Sign in in the VFS365 menu.",
            "VFS365 kon u niet zelf aanmelden: {0}. Klik hier, of kies Aanmelden in het VFS365-menu."),
        ["Recovered.Title"] = ("Uploading earlier changes", "Eerdere wijzigingen uploaden"),
        ["Recovered.Text"] = ("{0} file(s) changed before VFS365 last stopped are being uploaded.",
            "{0} bestand(en) die zijn gewijzigd voordat VFS365 stopte, worden geüpload."),
        ["NoOneDrive.Title"] = ("No OneDrive", "Geen OneDrive"),
        ["NoOneDrive.Text"] = ("Your account has no OneDrive, so this drive is empty. Ask your IT department whether you should have one, or whether VFS365 should show SharePoint sites.",
            "Uw account heeft geen OneDrive, dus dit station is leeg. Vraag uw IT-afdeling of u er een zou moeten hebben, of dat VFS365 SharePoint-sites moet tonen."),
        ["Policy.Title"] = ("Settings changed", "Instellingen gewijzigd"),
        ["Policy.Text"] = ("Your organisation changed VFS365's settings ({0}). They apply after Restart in the VFS365 menu, or at the next sign-in.",
            "Uw organisatie heeft instellingen van VFS365 gewijzigd ({0}). Ze gelden na Opnieuw starten in het VFS365-menu, of bij de volgende aanmelding."),
        ["Letter.Title"] = ("Drive letter not available", "Stationsletter niet beschikbaar"),
        ["Letter.Text"] = ("{0} is in use or not valid, so your files are in the navigation pane and at {1}, without a drive letter.",
            "{0} is in gebruik of ongeldig. Uw bestanden staan daarom zonder stationsletter in het navigatiedeelvenster en op {1}."),
        ["Letter.TextNoPane"] = ("{0} is in use or not valid, so your files are at {1}, without a drive letter.",
            "{0} is in gebruik of ongeldig. Uw bestanden staan daarom zonder stationsletter op {1}."),
        ["Security.Title"] = ("Security warning", "Beveiligingswaarschuwing"),
        ["Security.Text"] = ("Another user on this PC has taken {0}. VFS365 didn't start your drive, so nothing can be saved there by mistake. Tell your IT department.",
            "Een andere gebruiker op deze pc heeft {0} in gebruik. VFS365 heeft uw station niet gestart, zodat daar niets per ongeluk wordt opgeslagen. Meld dit bij uw IT-afdeling."),
        ["Security.SameName"] = ("Another account with your user name ({1}) has {0} on this PC. VFS365 didn't start your drive, so nothing can be saved there by mistake. Sign that account out of this PC, or tell your IT department.",
            "Een ander account met uw gebruikersnaam ({1}) heeft {0} in gebruik op deze pc. VFS365 heeft uw station niet gestart, zodat daar niets per ongeluk wordt opgeslagen. Meld dat account af op deze pc, of meld dit bij uw IT-afdeling."),
        ["Failed.Title"] = ("VFS365 didn't start", "VFS365 is niet gestart"),
        ["Failed.Text"] = ("{0}. Choose Retry in the VFS365 menu, or Show log for the details.",
            "{0}. Kies Opnieuw proberen in het VFS365-menu, of Logboek weergeven voor de details."),
        ["Stopped.Title"] = ("VFS365 stopped", "VFS365 is gestopt"),
        ["Stopped.Text"] = ("VFS365 stopped unexpectedly several times. Choose Retry in the VFS365 menu to start it again, or Show log for the details.",
            "VFS365 is meerdere keren onverwacht gestopt. Kies Opnieuw proberen in het VFS365-menu om het weer te starten, of Logboek weergeven voor de details."),
        ["Diagnostics.Title"] = ("Diagnostics not collected", "Diagnosegegevens niet verzameld"),
        ["Diagnostics.Failed"] = ("VFS365 couldn't collect its diagnostics: {0}", "VFS365 kon de diagnosegegevens niet verzamelen: {0}"),
        ["Shutdown.Uploading"] = ("Uploading {0} changed file(s) to Microsoft 365", "{0} gewijzigd(e) bestand(en) uploaden naar Microsoft 365"),

        // Explorer's VFS365 menu (ExplorerVerbs, ExplorerMenu); its items have no access keys, Explorer gives them none in a submenu either
        ["Verb.Menu"] = ("VFS365", "VFS365"),
        ["Verb.browser"] = ("Open in browser", "Openen in browser"),
        ["Verb.desktop"] = ("Open in desktop app", "Openen in bureaublad-app"),
        ["Verb.link"] = ("Copy link", "Koppeling kopiëren"),
        ["Verb.versions"] = ("Version history", "Versiegeschiedenis"),
        ["Verb.share"] = ("Share in browser", "Delen in browser"),
        ["Verb.keep"] = ("Keep this version", "Deze versie behouden"),
        ["Verb.compare"] = ("Compare with the original", "Vergelijken met het origineel"),
        ["Verb.NotRunning"] = ("VFS365 isn't running, so it can't do this now. It starts when you sign in to Windows.",
            "VFS365 is niet actief en kan dit nu niet doen. Het start wanneer u zich aanmeldt bij Windows."),
        ["Verb.NotOnDrive"] = ("{0} isn't in your VFS365 drive.", "{0} staat niet in uw VFS365-station."),
        ["Verb.NotFound"] = ("{0} wasn't found in Microsoft 365. It may have been moved or deleted.",
            "{0} is niet gevonden in Microsoft 365. Mogelijk is het verplaatst of verwijderd."),
        ["Verb.NotUploaded"] = ("{0} isn't in Microsoft 365 yet. Try again once it is uploaded.",
            "{0} staat nog niet in Microsoft 365. Probeer het opnieuw zodra het is geüpload."),
        ["Verb.OwnFolder"] = ("{0} is a folder VFS365 shows to group your libraries; it has no page in Microsoft 365.",
            "{0} is een map waarin VFS365 uw bibliotheken groepeert; het heeft geen pagina in Microsoft 365."),
        ["Verb.NotOffice"] = ("Open in desktop app works for Word, Excel and PowerPoint files.",
            "Openen in bureaublad-app werkt voor Word-, Excel- en PowerPoint-bestanden."),
        ["Verb.NoVersions"] = ("Microsoft 365 has no version history page for {0}.", "Microsoft 365 heeft geen pagina met versiegeschiedenis voor {0}."),
        ["Verb.NotCopy"] = ("{0} isn't a copy VFS365 made of a file that changed in Microsoft 365.",
            "{0} is geen kopie die VFS365 heeft gemaakt van een bestand dat in Microsoft 365 is gewijzigd."),
        ["Verb.Failed"] = ("{0}: {1}.", "{0}: {1}."),
        ["Verb.Copied.Title"] = ("Link copied", "Koppeling gekopieerd"),
        ["Verb.Copied"] = ("The link to {0} is on the clipboard. It works for people who already have access.",
            "De koppeling naar {0} staat op het klembord. Die werkt voor mensen die al toegang hebben."),
        ["Verb.Share.Title"] = ("Share", "Delen"),
        ["Verb.ShareHint"] = ("{0} is open in your browser. Choose Share there.", "{0} is geopend in uw browser. Kies daar Delen."),
        ["Verb.Kept.Title"] = ("Your version is kept", "Uw versie is behouden"),
        ["Verb.Kept"] = ("{0} now has your version. The other version stays in its version history, and the copy is deleted.",
            "{0} heeft nu uw versie. De andere versie blijft in de versiegeschiedenis, en de kopie is verwijderd."),
        ["Verb.ChangedAgain"] = ("{0} was changed again in Microsoft 365. Compare it with your copy before you keep your version.",
            "{0} is opnieuw gewijzigd in Microsoft 365. Vergelijk het met uw kopie voordat u uw versie behoudt."),
        ["Verb.search"] = ("Search in Microsoft 365", "Zoeken in Microsoft 365"),

        // Office files on the drive (OfficeVerbs, OfficeOpen): the default verb's label, and the question for a type without an app
        ["Office.Open"] = ("Open from Microsoft 365", "Openen vanuit Microsoft 365"),
        ["Office.OpenBrowser"] = ("Open in the browser", "Openen in de browser"),
        ["Office.OpenAsk"] = ("Open", "Openen"),
        ["Office.Ask.Title"] = ("Open file", "Bestand openen"),
        ["Office.Ask.Text"] = ("{0} isn't installed on this PC. Open {1} in {0} for the web, or choose another app on this PC?",
            "{0} is niet geïnstalleerd op deze pc. {1} openen in {0} voor het web, of een andere app op deze pc kiezen?"),
        ["Office.Ask.Browser"] = ("&Open in the browser", "&Openen in de browser"),
        ["Office.Ask.App"] = ("&Choose an app", "&App kiezen"),
        ["Office.Ask.Cancel"] = ("Cancel", "Annuleren"),

        // Searching with Microsoft 365 (SearchActions, AskWindow, ExplorerSearchWatcher)
        ["Menu.Search"] = ("Search &Microsoft 365...", "Zoeken in &Microsoft 365..."),
        ["Search.Title"] = ("Search in Microsoft 365", "Zoeken in Microsoft 365"),
        ["Search.Prompt"] = ("Find files in {0}:", "Bestanden zoeken in {0}:"),
        ["Search.Everything"] = ("all of Microsoft 365", "heel Microsoft 365"),
        ["Search.Button"] = ("Search", "Zoeken"),
        ["Search.Cancel"] = ("Cancel", "Annuleren"),
        ["Search.HandedOff.Title"] = ("Search results in your browser", "Zoekresultaten in uw browser"),
        ["Search.HandedOff"] = ("VFS365 searched Microsoft 365 for \"{0}\": the results are in your browser.",
            "VFS365 heeft in Microsoft 365 gezocht naar \"{0}\": de resultaten staan in uw browser."),
        ["Search.Stopped"] = ("Searching the drive in Explorer reads every folder from Microsoft 365, so VFS365 stopped it. Right-click a folder and choose VFS365 > Search in Microsoft 365.",
            "Zoeken op het station in Verkenner leest elke map uit Microsoft 365, daarom heeft VFS365 het gestopt. Klik met de rechtermuisknop op een map en kies VFS365 > Zoeken in Microsoft 365."),
        ["Search.NoPage"] = ("{0} can't be searched in Microsoft 365.", "{0} kan niet worden doorzocht in Microsoft 365."),

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
        ["Aad.Unavailable"] = ("Windows' sign-in (Web Account Manager) isn't working", "de aanmelding van Windows (Web Account Manager) werkt niet"),
        ["Aad.Other"] = ("sign-in failed", "aanmelden is mislukt"),
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
