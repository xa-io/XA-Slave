using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using XASlave.Localization;

namespace XASlave.Windows;

/// <summary>
/// Commands reference panel for the current XA Slave slash-command surface.
/// </summary>
public partial class SlaveWindow
{
    private sealed record CommandReferenceEntry(string Command, string PurposeKey, string NotesKey)
    {
        public string Purpose => UiLocale.Get(PurposeKey);
        public string Notes => UiLocale.Get(NotesKey);
        public string SearchHaystack => UiLocale.Culture.TextInfo.ToLower($"{Command}\n{Purpose}\n{Notes}");
    }

    private string commandsSearchText = string.Empty;
    private string commandsCachedSearchText = string.Empty;
    private int commandsCachedLocaleRevision = -1;
    private string[] commandsLowerQueryTerms = [];
    private readonly Dictionary<CommandReferenceEntry[], CommandReferenceEntry[]> commandsFilteredEntries = [];

    private static readonly CommandReferenceEntry[] GeneralCommandEntries =
    {
        new("/xa", "S559AA622848DA484", "S6729A7485E4994B2"),
        new("/xa win <name>", "S4E7CCC3E20AACE50", "S716DEEB3E539AF88"),
        new("/xa xagman mini | /xa xagman m", "SE1EC892A5FE19629", "S5391B882F15D5E35"),
        new("/xa allrestore", "SEFE504C50E19DCDD", "SE7E8755F3E0DBF3A"),
        new("/xa commands", "S60447572EF1DCF96", "S67C6C73B20108D6B"),
        new("/xa updates", "S591D57C4F8F2E5E4", "S5B487647489A3359"),
        new("/xa db <itemId:qty ...>", "SB78BD5BDF2014E43", "S211651C31C9F06AE"),
        new("/xa db inv", "S43E304A6C47DCB38", "SB0CCBBE65209EBC9"),
        new("/xa db clear", "S25D24D5BA2B8840B", "S44D20A80019EB84F"),
        new("/xa db begin", "S3ADC0818E77CF058", "S871DD2AF9AB674D1"),
        new("/xa db request <itemId:qty ...>", "S3611AD16522C88CD", "S78553CD46E3E8756"),
        new("/xa db <shortcut>", "S5EFE9FB25E88206D", "SEDCBD9E9B66DAC9F"),
        new("/xa db subloot", "S7BC0CF00353FFACE", "SC401BFD853EBA234"),
        new("/xa db subgil <gil-value>", "SE189D63425C4C0DA", "S9DCBD161CCE523F5"),
        new("/xa db subsupplies <days>", "SB92DD087C2AD5DB1", "S298099EEF7AA184D"),
        new("/xa npcsell <subloot|itemId,itemId,...>", "S21E9EC3895AE14F5", "S63D035A05165EEF6"),
        new("/xa debug", "S2595690480D1A8C6", "S14EC2DA051DCDFE7"),
        new("/xa sort", "SC86305BA8A97F090", "SA6F0F8B833751197"),
        new("/xa preset list", "S6520A964E130B7B0", "S2AB2E0A975FC8B15"),
        new("/xa preset load <name>", "SC0F83B07F032B537", "S0B243E521F374204"),
        new("/xa preset save <name>", "SA6B0637371D7E12E", "SEFB1F5EAE6FF5975"),
        new("/xa xamods | /xa mods", "SE38516FD03B6EB81", "S491E035A8632F078"),
    };

    private static readonly CommandReferenceEntry[] GameModsCommandEntries =
    {
        new("/xa clearlog", "S525AC46D6D432DF6", "S94217AC7F74A697A"),
        new("/xa chocobocutscene on|off", "S8F540FDDA5EB58B5", "S2149784C39C936B9"),
        new("/xa closeerrors on|off", "S45F7B3137669A582", "SF6B242DFE5BD1CCB"),
        new("/xa gamerestore", "SEB139D7B83029ADA", "S17A8A017466342B9"),
        new("/xa lockcombat on|off", "SC6399C54BA7F49BA", "SD152DF217B0C17CC"),
        new("/xa logincooldown on|off", "SDCB6B531CC6AC67E", "SC5F512B1D65A47A6"),
        new("/xa multiinstance on|off", "SAAA8CE511A14CD50", "S6649519E36332FB8"),
        new("/xa preventlobbyexit on|off", "SE7182D7C4E3AB285", "SD62A02733CE86800"),
        new("/xa queueposition on|off", "S825BF42D898C87C2", "S4DDE4870AEE4F22C"),
        new("/xa skipcutscenes on|off", "SA34E45C375791613", "SE890035D4E0EFA74"),
        new("/xa skipdialogue on|off", "SA6771B5E4B120506", "SEA190C45A962C27A"),
        new("/xa targetfix on|off", "SE8D8BA4631BD6518", "SA08C366D54648DE7"),
    };

    private static readonly CommandReferenceEntry[] UiModsCommandEntries =
    {
        new("/xa anonymous on|off", "S8654D6DE094CBB08", "S36D42E20ABF8C875"),
        new("/xa castbar on|off", "SBAFFB9FBF18018D3", "SDD1FF0ABD7046443"),
        new("/xa copyitemname on|off", "S8F310CCDB6D9F132", "S843B73331A4472FD"),
        new("/xa dalamudnotifs on|off", "S15DA66142245B846", "S2C74D882B6E85EBA"),
        new("/xa displayids on|off", "S4252171BE0788CDE", "S7EA09C0DD3850931"),
        new("/xa dutyfinder on|off", "SB9B6A08902E51FEC", "S7621348B89885E98"),
        new("/xa highlighttargets on|off", "S2A43C828FBD3150E", "S7EF8FD9CFB0D2AD5"),
        new("/xa latency on|off", "S94C45C2378407015", "S8EDD237D0AC7FC98"),
        new("/xa msqprogress on|off", "SD2EE3386B1BB409D", "S244F77398730C1C9"),
        new("/xa playersearch on|off", "S220EB7B49BEC81EC", "S6C8536E6E3F7CADF"),
        new("/xa shopicons on|off", "SDA5B535604BB493D", "S7A22C3AC183652C4"),
        new("/xa timestampseconds on|off", "S7D38D5D7854CB759", "S84A64478D388C171"),
        new("/xa uirestore", "SC5616C0DE61C2122", "S9489459FCBCF28BF"),
    };

    private static readonly CommandReferenceEntry[] GraphicModsCommandEntries =
    {
        new("/xa bgpause on|off", "S2851442A7A6BC36A", "S01577BBE640EC32A"),
        new("/xa customres on|off", "S9213B3FFD39F3B0E", "S40D5D8C722AC6978"),
        new("/xa hidepopups on|off", "S63A6BDC7FE6F4E86", "SAAC3CAFA734A7C81"),
        new("/xa hideobjects on|off", "S659958DC4B409D64", "S724964C8C91C748D"),
        new("/xa lowres on", "S0C5701F6DEB91EBA", "S6D992C380AB87860"),
        new("/xa lowres <scale>", "S15C52FBF97A81535", "S0B5B7EAAC6BF8327"),
        new("/xa lowres off", "S00455185F837E752", "S5BCFA225E5A3D26B"),
        new("/xa minwindow on|off", "SA07BB20CC5061D28", "S0900EC76642E7556"),
        new("/xa nouifade on|off", "S091281AFEC059B39", "S578475BDF42E1065"),
        new("/xa res <width>x<height> | <width> <height>", "SD6A8EED97D889CE5", "S193F773061D86BF7"),
        new("/xa res reset", "SB99467A6B902EADD", "SA3E33AC5965C08AB"),
        new("/xa res add <width>x<height>", "SB74E49EB977B9874", "SED24158BF7582B0B"),
        new("/xa res remove <width>x<height>", "S130B9B47C252B98F", "SD34B092F404E59FC"),
        new("/xa resrestore", "S22D2DAAB93510F5B", "S6767CC3A46360A27"),
        new("/xa specialrender on|off", "SC46E6FCEEACA4E38", "S798EFB94662F125B"),
        new("/xa titlemovie on|off", "S9515516597DF8478", "SDD98DAE617E959C1"),
    };

    private static readonly CommandReferenceEntry[] PlayerModsCommandEntries =
    {
        new("/xa antiafk on|off", "S059A237494348EBE", "S0635770AF4AF4C9B"),
        new("/xa dutycommence on|off", "SAA110482D132D3CC", "S5C0F1B04B10A5021"),
        new("/xa companychest on|off", "SA30C4D9A04DCC1CA", "S282513C381F72989"),
        new("/xa equip <itemId>", "SDE70919C8A16F0B3", "S4C59F1DC6855F943"),
        new("/xa doze", "S9A18479FCEDC590E", "SA8D744E283531056"),
        new("/xa expertdelivery on|off", "S368DCE2AA3D07E78", "S5EC010BFB585D661"),
        new("/xa friendnear on|off", "S9BF425164189A16E", "S2BC005808A88C07C"),
        new("/xa typingcombat on|off", "SC0D23B688EAA7EA6", "SC982020A49DED82A"),
        new("/xa inventorymover on|off", "S75848D0A2768D172", "SADA46082DE3431A2"),
        new("/xa itemcommands on|off", "S834EDAF55A7B84CC", "S60A663C0BF78B4F3"),
        new("/xa leaveduty on|off", "SC0B2E21F7FE3FFD4", "S935E3D3F4B2850E2"),
        new("/xa automerge on|off", "S78E0FB04C6738B92", "S1BC21069D04BA1C4"),
        new("/xa mooglemail on|off", "S8F1505CFD3E6BCDC", "SA498B9D9240528D8"),
        new("/xa mail", "S461703933AC51890", "S9907151B8FF2B07F"),
        new("/xa sit", "S435171A41E10C4D6", "SA8D744E283531056"),
        new("/xa nearby [on|off]", "SF3379D4CB16C41F1", "S2ECC30DE7B76EC4F"),
        new("/xa peep [on|off|clear]", "S6EA5E8D6F79F8BAB", "S0B9994D245600F83"),
        new("/xa viewitem", "S529D72558DCE307B", "S964D13A3E52C0D60"),
        new("/xa outfits [player]", "SEE5DE7DFC48A65F8", "SA37B5CCAC11B101A"),
        new("/xa playerrestore", "S017E3B879652F954", "SAADA754EC06DC59C"),
        new("/xa refusetrade on|off", "S8A6DB441B7325AFE", "S6B50A646A37A46F5"),
        new("/xa revealmap on|off", "SAA6F959BD5D343E8", "S961DCDDAC41325AD"),
        new("/xa blacklistedparty on|off", "S62C80C5D10627FEE", "S296212A4646A5043"),
        new("/xa sightdistance on|off", "S1C32A68579E7AC51", "SA0847B5AB6C47427"),
        new("/xa sitdoze on|off", "SEEAAE10581D507DF", "S07D7ACB382070DB2"),
        new("/xa sprint on|off", "S80C3B434EFE3A8DB", "S8ECEF1C7A2FB7D58"),
        new("/xa sprintdelay <seconds>", "SB3FC8B06C909ECC3", "S7FEC21448905B1AE"),
        new("/xa teleportlock on|off", "S8824A0557E16C450", "S24EC9EA18063F94D"),
        new("/xa titlesasplayernames on|off", "S7C634C81C7DAA01E", "S86A27E3E47E08780"),
        new("/xa travelerworlds on|off", "S33BE009FB07B4BCB", "SEC5EAC34D26CFDF4"),
    };

    private static readonly CommandReferenceEntry[] PluginModsCommandEntries =
    {
        new("/xa anonchars on|off", "S266CA67965EE933C", "SA2D5AB8812194867"),
        new("/xa teleporthelper on|off", "S0575D35416C789B9", "SA972C375771234E9"),
        new("/xa peepingtom on|off", "SDF4C9DC63B5A41FD", "S8F2C801EA6E92F84"),
        new("/xa pluginrestore", "S6AD14B4AB396A456", "SB89B14121F13B598"),
    };

    private static readonly CommandReferenceEntry[] EurekaModsCommandEntries =
    {
        new("/xa eurekaid on|off", "SD41808D6B16B9D9E", "SC4B6173DD63C3022"),
        new("/xa eurekarestore", "S39502BAE77DCF340", "SF135C9E01C817F39"),
        new("/xa fe <entry>", "SE3820C3DDB0DACF9", "SFA78B955DE399DC1"),
        new("/xa fieldentrycommand on|off", "S84BC9A92EA60F43C", "S75EFADCFAA0DA77D"),
    };

    private static readonly CommandReferenceEntry[] IllegalModsCommandEntries =
    {
        new("/xa imlegit", "S217675DA65212B46", "SA382BAA8A9C32098"),
        new("/xa instantreturn on|off", "S2C600D236B5780F9", "S3189A1ECC1623220"),
        new("/xa instantlogout on|off", "S5A8C3422028C075E", "S6C898225B3DB0949"),
        new("/xa logout", "SD2E802F3B73EB1D7", "S1C38F9650AEE0738"),
        new("/xa killgame", "SE10D5839C94EAD37", "SA55F73A5A007DBF1"),
        new("/xa moveafterdeath on|off", "S5A49F2937AAA1FBB", "S23786B5BB9AFF34D"),
        new("/xa unlockexpert on|off", "S3CBF8C11F9A231CA", "S352CBD381424F686"),
    };

    private static readonly CommandReferenceEntry[] XaMovementCommandEntries =
    {
        new("/xa movingcheatersmart", "S29A089CA159D2BAB", "S6EBA27A01230C93E"),
        new("/xa movingcheaterfly", "S958CCD45BCE169FC", "SDFB4D6F7E16226E4"),
        new("/xa movingcheaterwalk", "S98E210CCC90C9FCB", "S897B06A6DF2540CC"),
        new("/xa interact", "SD3EE2130B989888D", "SC2D8A4A88E89B54E"),
        new("/xa leaveduty", "S5CD58FA7DF7B3937", "S02D0B739E005B648"),
        new("/xa recommendedgear", "S88849509B9C7DF45", "S8D7C8CADE1278095"),
        new("/xa stopmovement", "SB81FC5A6B6D1214C", "S4D6D4B2CD22743CE"),
        new("/xa pathtotargetinteract", "S44AEDDF0DB48AE1D", "SC8A141A56A713ED4"),
        new("/xa pathsmartinteract", "S347F32AE00CA36A1", "S6A68A1569B03274D"),
    };

    private void DrawCommandsReference()
    {
        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), global::XASlave.Localization.UiLocale.Get("SB269DC4E81A528A4"));
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S2439CDD799EBC53D"));

        ImGui.Spacing();
        ImGui.SetNextItemWidth(Math.Max(Scale(220f), ImGui.GetContentRegionAvail().X));
        ImGui.InputTextWithHint(
            "##CommandsSearch",
            global::XASlave.Localization.UiLocale.Get("S939ECE14A09CA1DB"),
            ref commandsSearchText,
            256);

        if (commandsCachedLocaleRevision != UiLocale.Revision || !string.Equals(commandsCachedSearchText, commandsSearchText, StringComparison.Ordinal))
        {
            commandsCachedSearchText = commandsSearchText;
            commandsCachedLocaleRevision = UiLocale.Revision;
            commandsLowerQueryTerms = commandsSearchText
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(term => UiLocale.Culture.TextInfo.ToLower(term))
                .ToArray();
            commandsFilteredEntries.Clear();
        }

        var visibleSectionCount = 0;
        DrawFilteredCommandSection(UiLocale.Native("SC910D474DCD724BF"), GeneralCommandEntries, ref visibleSectionCount);
        DrawFilteredCommandSection(UiLocale.Native("S21D4DD0AB4C3AE52"), GameModsCommandEntries, ref visibleSectionCount);
        DrawFilteredCommandSection(UiLocale.Native("S374ADB0F115F2717"), UiModsCommandEntries, ref visibleSectionCount);
        DrawFilteredCommandSection(UiLocale.Native("S29D926CC18BA302F"), GraphicModsCommandEntries, ref visibleSectionCount);
        DrawFilteredCommandSection(UiLocale.Native("S4FD5B97AAA9976CF"), PlayerModsCommandEntries, ref visibleSectionCount);
        DrawFilteredCommandSection(UiLocale.Native("S1C1183BF04995217"), PluginModsCommandEntries, ref visibleSectionCount);
        DrawFilteredCommandSection(UiLocale.Native("S4DA75721A5F544BC"), EurekaModsCommandEntries, ref visibleSectionCount);
        DrawFilteredCommandSection(UiLocale.Native("SF18B6FBE0208AC7E"), IllegalModsCommandEntries, ref visibleSectionCount);
        DrawFilteredCommandSection(UiLocale.Native("S64D40FC90C6D2322"), XaMovementCommandEntries, ref visibleSectionCount);

        if (visibleSectionCount == 0)
        {
            ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S643C7D8A9E6A7717"));
            ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S4AFEC65507FF3235"));
        }
    }

    private void DrawFilteredCommandSection(string title, CommandReferenceEntry[] entries, ref int visibleSectionCount)
    {
        var filteredEntries = FilterCommandEntries(entries);
        if (filteredEntries.Length == 0)
            return;

        visibleSectionCount++;
        DrawCommandSection(title, filteredEntries);
    }

    private CommandReferenceEntry[] FilterCommandEntries(CommandReferenceEntry[] entries)
    {
        if (commandsLowerQueryTerms.Length == 0)
            return entries;

        if (commandsFilteredEntries.TryGetValue(entries, out var cached))
            return cached;

        var matches = new List<CommandReferenceEntry>();
        foreach (var entry in entries)
        {
            if (MatchesCommandSearch(entry, commandsLowerQueryTerms))
                matches.Add(entry);
        }

        var result = matches.ToArray();
        commandsFilteredEntries[entries] = result;
        return result;
    }

    private static bool MatchesCommandSearch(CommandReferenceEntry entry, string[] queryTerms)
    {
        foreach (var term in queryTerms)
        {
            if (!entry.SearchHaystack.Contains(term, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private static void DrawCommandSection(string title, CommandReferenceEntry[] entries)
    {
        DrawWrappedDisabledText(UiLocale.T(title));

        using (var imguiScope251 = ImRaii.Table(
                $"CommandsReferenceTable##{title}",
                2,
                ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.NoSavedSettings | ImGuiTableFlags.PadOuterX))
        if (imguiScope251)
        {
            ImGui.TableSetupColumn("Command", ImGuiTableColumnFlags.WidthFixed, Scale(210f));
            ImGui.TableSetupColumn("Description", ImGuiTableColumnFlags.WidthStretch);

            for (var i = 0; i < entries.Length; i++)
            {
                var entry = entries[i];
                ImGui.TableNextRow();
                ImGui.TableSetBgColor(
                    ImGuiTableBgTarget.RowBg0,
                    ImGui.GetColorU32(i % 2 == 0
                        ? new Vector4(0.28f, 0.28f, 0.28f, 0.24f)
                        : new Vector4(0.03f, 0.03f, 0.03f, 0.24f)));

                ImGui.TableNextColumn();
                using (ImRaii.TextWrapPos(0f))
                {
                    ImGui.TextColored(new Vector4(0.88f, 0.92f, 0.98f, 1.0f), entry.Command);
                }

                ImGui.TableNextColumn();
                using (ImRaii.TextWrapPos(0f))
                {
                    ImGui.TextUnformatted($"{entry.Purpose} {entry.Notes}");
                }
            }


        }

        ImGui.Spacing();
    }
}
