using System;
using XASlave.Localization;

namespace XASlave.Services;

// Feature-local, exact artifact pairs. October remains a separate profile;
// November addresses are individually reconciled, never a relocation delta.
internal sealed class ViewSingleItemNativeContract
{
    internal const string OctoberGameBuild = "46A52AB1DBE80B9A8D6C4D4531BF66693904200C5135FACACD053EF28A5DE14A";
    internal const string NovemberGameBuild = "CEB4260B2574476ACF72E7B6105D0A4F8D1BD78B4E7CAB5D8115C453F2F39339";
    internal const string NovemberSdk = "2D268F54FB6C75B511C349BA49B00E8D197C09FF64FF380ACABDCAE42A1F7D9B";
    internal const string NovemberStableSdk = "28488F18AD05FDCD12373A83756EAB275C291B09C6AF061EF7F846AA06A36E29";
    private readonly bool november;

    private ViewSingleItemNativeContract(bool november) => this.november = november;
    internal string GameBuild => november ? NovemberGameBuild : OctoberGameBuild;
    internal string CaptureLayout => november ? ViewSingleItemCapture.NovemberLayout : ViewSingleItemCapture.SupportedLayout;

    internal static ViewSingleItemNativeContract Resolve(string gameBuild, string sdk)
    {
        if (gameBuild == OctoberGameBuild && sdk is
            ("A47750CE4BE1910C8783231F398559BCCE43E4A6AE8D255351B3BBA8247D6F9D"
            or "98795452ED0636E3613850176310AEF1E4E8B85AE47A3E722FF2C325CF2801D2"
            or "F5A2C68CCECD3E199BDA572050E98902B2E7594066C95DC7826C2D4297CD0564"))
            return new(false);
        if (gameBuild == NovemberGameBuild && sdk is (NovemberSdk or NovemberStableSdk))
            return new(true);
        throw UiMessage.Attach(new InvalidOperationException(UiLocale.Native("SF0B352CBCC662406")), UiMessage.Key("SF0B352CBCC662406"));
    }

    internal int ResolveRva(int octoberRva)
    {
        if (!november) return octoberRva;
        return octoberRva switch
        {
            0x8F270 => 0x8F270,
            0x90D40 => 0x90D40,
            0x2079F0 => 0x207A00,
            0x25D770 => 0x25D660,
            0x261160 => 0x261050,
            0x2692A0 => 0x269190,
            0x2692D0 => 0x2691C0,
            0x2692E0 => 0x2691D0,
            0x2693A0 => 0x269290,
            0x269430 => 0x269320,
            0x26AC50 => 0x26AB30,
            0x274830 => 0x274710,
            0x2748F0 => 0x2747D0,
            0x282A10 => 0x2828B0,
            0x2E6FE0 => 0x2E6CA0,
            0x2EE340 => 0x2EE000,
            0x334940 => 0x334620,
            0x338530 => 0x338210,
            0x339380 => 0x339060,
            0x343CE0 => 0x3439C0,
            0x343E20 => 0x343B00,
            0x41EC00 => 0x41E950,
            0x41FDE0 => 0x41FB30,
            0x420011 => 0x41FD61,
            0x446E00 => 0x446B50,
            0x4471C0 => 0x446F10,
            0x447320 => 0x447070,
            0x447620 => 0x447370,
            0x447810 => 0x447560,
            0x447980 => 0x4476D0,
            0x447A60 => 0x4477B0,
            0x447B10 => 0x447860,
            0x447BA0 => 0x4478F0,
            0x447C30 => 0x447980,
            0x4480E4 => 0x447E34,
            0x449220 => 0x448F70,
            0x449270 => 0x448FC0,
            0x4705F0 => 0x470340,
            0x607410 => 0x6070B0,
            0x607430 => 0x6070D0,
            0x607880 => 0x607520,
            0x6078C0 => 0x607560,
            0x614700 => 0x6143A0,
            0x614F90 => 0x614C30,
            0x614FD0 => 0x614C70,
            0x898910 => 0x898980,
            0x899220 => 0x899290,
            0x899270 => 0x8992E0,
            0x8992D0 => 0x899340,
            0x89E0C0 => 0x89E130,
            0x8BECD0 => 0x8BED40,
            0x8BFF10 => 0x8BFF80,
            0x8C9D80 => 0x8C9DF0,
            0x8D0FA0 => 0x8D1010,
            0x8D15D8 => 0x8D1648,
            0x8D3C00 => 0x8D3C70,
            0x8D4850 => 0x8D48C0,
            0x8D4870 => 0x8D48E0,
            0x8D4CB0 => 0x8D4D20,
            0x8D4EB0 => 0x8D4F20,
            0x8D7420 => 0x8D7490,
            0x8D7750 => 0x8D77C0,
            0x8DC120 => 0x8DC190,
            0x8DC1E0 => 0x8DC250,
            0x8DC690 => 0x8DC700,
            0x8DC6E0 => 0x8DC750,
            0x906F60 => 0x906FD0,
            0x9075B0 => 0x907620,
            0x9077B0 => 0x907820,
            0x907AD0 => 0x907B40,
            0x907BB0 => 0x907C20,
            0x90A2A0 => 0x90A310,
            0x9CDEF0 => 0x9CDD40,
            0x9CE150 => 0x9CDFA0,
            0x9CE2A0 => 0x9CE0F0,
            0x9CE340 => 0x9CE190,
            0x9CE3B0 => 0x9CE200,
            0x9CE4A0 => 0x9CE2F0,
            0x9CE7E0 => 0x9CE630,
            0x9CEE00 => 0x9CEC50,
            0x9CF840 => 0x9CF690,
            0x9CF960 => 0x9CF7B0,
            0x9CF9F0 => 0x9CF840,
            0x9DAE40 => 0x9DAD70,
            0x9DC240 => 0x9DC200,
            0x9DC5F0 => 0x9DC5B0,
            0x9DD2D0 => 0x9DD300,
            0x1759180 => 0x1759060,
            0x17595B0 => 0x1759490,
            0x17598D0 => 0x17597B0,
            0x18CB620 => 0x18CBCE0,
            0x18CB690 => 0x18CBD50,
            0x1992690 => 0x1992CE0,
            0x1F08A60 => 0x1F09430,
            0x1F08FA0 => 0x1F09970,
            0x214D248 => 0x214E608,
            0x216C9D8 => 0x216DAE8,
            0x21A1CC8 => 0x21A4008,
            0x21A5A28 => 0x21A7EC8,
            0x21C5F40 => 0x21C86B0,
            0x22D82C8 => 0x22DB208,
            0x28BCB30 => 0x28B9B40,
            0x28BCB80 => 0x28B9B90,
            0x28BCBD0 => 0x28B9BE0,
            0x28BCC20 => 0x28B9C30,
            _ => throw UiMessage.Attach(new InvalidOperationException(UiLocale.Native("S2466DF1446E291FA")), UiMessage.Key("S2466DF1446E291FA")),
        };
    }
}
