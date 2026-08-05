using System.Collections.Generic;
using RateDesk.Core.Config;

namespace RateDesk.Tests
{
    /// <summary>In-code miniature configs for engine tests (independent of shipped JSON).</summary>
    public static class TestConfigs
    {
        public static CurrencyConfig Usd() => new()
        {
            Ccy = "USD",
            Calendar = "USD",
            SpotLag = 2,
            DefaultSource = "BGN",
            DefaultProduct = "OIS",
            Discounting = "OIS",
            Ois = new OisConfig
            {
                IndexName = "SOFR",
                IndexDcc = "ACT/360",
                FixedFreq = "Annual",
                FixedDcc = "ACT/360",
                PayLag = 2,
                Curve = new List<PillarDef>
                {
                    new() { Tenor = "1M", Type = "OIS", Ticker = "USOSFRA" },
                    new() { Tenor = "3M", Type = "OIS", Ticker = "USOSFRC" },
                    new() { Tenor = "6M", Type = "OIS", Ticker = "USOSFRF" },
                    new() { Tenor = "1Y", Type = "OIS", Ticker = "USOSFR1" },
                    new() { Tenor = "2Y", Type = "OIS", Ticker = "USOSFR2" },
                    new() { Tenor = "3Y", Type = "OIS", Ticker = "USOSFR3" },
                    new() { Tenor = "5Y", Type = "OIS", Ticker = "USOSFR5" },
                    new() { Tenor = "7Y", Type = "OIS", Ticker = "USOSFR7" },
                    new() { Tenor = "10Y", Type = "OIS", Ticker = "USOSFR10" },
                    new() { Tenor = "20Y", Type = "OIS", Ticker = "USOSFR20" },
                    new() { Tenor = "30Y", Type = "OIS", Ticker = "USOSFR30" },
                },
            },
            Ladders = new List<Ladder>
            {
                new()
                {
                    Name = "CPI", Kind = "INFLATION", Aliases = new List<string> { "cpi", "uscpi" },
                    Dcc = "ACT/360", FixingTicker = "CPURNSA Index",
                    Pillars = new List<PillarDef>
                    {
                        new() { Tenor = "1Y", Ticker = "USSWIT1" },
                        new() { Tenor = "5Y", Ticker = "USSWIT5" },
                        new() { Tenor = "10Y", Ticker = "USSWIT10" },
                    },
                },
            },
        };

        public static CurrencyConfig Gbp() => new()
        {
            Ccy = "GBP",
            Calendar = "GBP",
            SpotLag = 0,
            DefaultProduct = "OIS",
            Discounting = "OIS",
            Ois = new OisConfig
            {
                IndexName = "SONIA",
                IndexDcc = "ACT/365F",
                FixedFreq = "Annual",
                FixedDcc = "ACT/365F",
                PayLag = 0,
                Curve = new List<PillarDef>
                {
                    new() { Tenor = "3M", Type = "OIS", Ticker = "BPSWSC" },
                    new() { Tenor = "1Y", Type = "OIS", Ticker = "BPSWS1" },
                    new() { Tenor = "5Y", Type = "OIS", Ticker = "BPSWS5" },
                    new() { Tenor = "10Y", Type = "OIS", Ticker = "BPSWS10" },
                    new() { Tenor = "30Y", Type = "OIS", Ticker = "BPSWS30" },
                },
            },
        };

        public static CurrencyConfig Aud() => new()
        {
            Ccy = "AUD",
            Calendar = "AUD",
            SpotLag = 1,
            DefaultProduct = "IRS",
            Discounting = "OIS",
            Ois = new OisConfig
            {
                IndexName = "AONIA",
                IndexDcc = "ACT/365F",
                FixedFreq = "Annual",
                FixedDcc = "ACT/365F",
                PayLag = 1,
                Curve = new List<PillarDef>
                {
                    new() { Tenor = "3M", Type = "OIS", Ticker = "ADSOC" },
                    new() { Tenor = "1Y", Type = "OIS", Ticker = "ADSO1" },
                    new() { Tenor = "3Y", Type = "OIS", Ticker = "ADSO3" },
                    new() { Tenor = "5Y", Type = "OIS", Ticker = "ADSO5" },
                    new() { Tenor = "10Y", Type = "OIS", Ticker = "ADSO10" },
                },
            },
            Irs = new IrsConfig
            {
                Legs = new List<IrsLegConv>
                {
                    new()
                    {
                        MaxTenor = "3Y", FixedFreq = "Quarterly", FixedDcc = "ACT/365F",
                        FloatIndex = "BBSW", FloatTenor = "3M", FloatFreq = "Quarterly", FloatDcc = "ACT/365F",
                        FixingDays = 0,
                    },
                    new()
                    {
                        MaxTenor = null, FixedFreq = "Semiannual", FixedDcc = "ACT/365F",
                        FloatIndex = "BBSW", FloatTenor = "6M", FloatFreq = "Semiannual", FloatDcc = "ACT/365F",
                        FixingDays = 0,
                    },
                },
                Curve = new List<PillarDef>
                {
                    new() { Tenor = "1Y", Type = "SWAP", Ticker = "ADSWAP1Q" },
                    new() { Tenor = "2Y", Type = "SWAP", Ticker = "ADSWAP2Q" },
                    new() { Tenor = "3Y", Type = "SWAP", Ticker = "ADSWAP3Q" },
                    new() { Tenor = "4Y", Type = "SWAP", Ticker = "ADSWAP4" },
                    new() { Tenor = "5Y", Type = "SWAP", Ticker = "ADSWAP5" },
                    new() { Tenor = "7Y", Type = "SWAP", Ticker = "ADSWAP7" },
                    new() { Tenor = "10Y", Type = "SWAP", Ticker = "ADSWAP10" },
                },
            },
        };

        /// <summary>AUD with ROLLING AxB FRA pillars added, for the FRA grammar/classifier/engine
        /// tests. A separate builder rather than an addition to <see cref="Aud"/>, so the shared
        /// fixture the other tests bootstrap against is untouched.</summary>
        public static CurrencyConfig AudWithFras()
        {
            var cfg = Aud();
            cfg.Irs!.Curve.InsertRange(0, new List<PillarDef>
            {
                new() { Tenor = "3X6", Type = "FRA", Ticker = "ADFR3X6" },
                new() { Tenor = "6X9", Type = "FRA", Ticker = "ADFR6X9" },
            });
            return cfg;
        }

        /// <summary>SEK: IMM-dated quarterly FRA strip expressed as FRA pillars with a PLAIN index
        /// period ("3M"), whose contract dates come from the snapshot MATURITY.</summary>
        public static CurrencyConfig Sek() => new()
        {
            Ccy = "SEK",
            Calendar = "SEK",
            SpotLag = 2,
            DefaultSource = "BGN",
            DefaultProduct = "IRS",
            Discounting = "SELF",
            Irs = new IrsConfig
            {
                Bdc = "ModifiedFollowing",
                Legs = new List<IrsLegConv>
                {
                    new()
                    {
                        MaxTenor = null, FixedFreq = "Annual", FixedDcc = "30/360",
                        FloatIndex = "STIBOR", FloatTenor = "3M", FloatFreq = "Quarterly",
                        FloatDcc = "ACT/360", FixingDays = 2,
                    },
                },
                Curve = new List<PillarDef>
                {
                    new() { Tenor = "3M", Type = "FRA", Ticker = "SKFR1" },
                    new() { Tenor = "1Y", Type = "SWAP", Ticker = "SKSW1" },
                    new() { Tenor = "2Y", Type = "SWAP", Ticker = "SKSW2" },
                    new() { Tenor = "5Y", Type = "SWAP", Ticker = "SKSW5" },
                    new() { Tenor = "10Y", Type = "SWAP", Ticker = "SKSW10" },
                },
            },
        };

        /// <summary>NOK: no FRA pillars at all — the IMM strip lives in Irs.FrontFromOis.FraTickers,
        /// and the strip is 3M even though the long leg is 6M NIBOR. This is the shape that makes
        /// reading the index period off the swap LEG wrong.</summary>
        public static CurrencyConfig Nok() => new()
        {
            Ccy = "NOK",
            Calendar = "NOK",
            SpotLag = 2,
            DefaultSource = "BGN",
            DefaultProduct = "IRS",
            Discounting = "SELF",
            Irs = new IrsConfig
            {
                Bdc = "ModifiedFollowing",
                Legs = new List<IrsLegConv>
                {
                    new()
                    {
                        MaxTenor = null, FixedFreq = "Annual", FixedDcc = "30/360",
                        FloatIndex = "NIBOR", FloatTenor = "6M", FloatFreq = "Semiannual",
                        FloatDcc = "ACT/360", FixingDays = 2,
                    },
                },
                FrontFromOis = new OisFrontDef
                {
                    StripTenor = "3M",
                    FraTickers = new List<string> { "NKF30001 Index", "NKF30002 Index" },
                },
                Curve = new List<PillarDef>
                {
                    new() { Tenor = "1Y", Type = "SWAP", Ticker = "NKSW1" },
                    new() { Tenor = "2Y", Type = "SWAP", Ticker = "NKSW2" },
                    new() { Tenor = "5Y", Type = "SWAP", Ticker = "NKSW5" },
                    new() { Tenor = "10Y", Type = "SWAP", Ticker = "NKSW10" },
                },
            },
        };

        /// <summary>BRL: a ladder-ONLY currency (no bootstrapped OIS/IRS) whose DI ladder quotes
        /// BUS/252 exponential zero rates. Deliberately its own builder rather than an addition to
        /// the shared fixture set, since it is the only config with no curve at all.</summary>
        public static CurrencyConfig Brl() => new()
        {
            Ccy = "BRL",
            Calendar = "BRL",
            SpotLag = 0,
            DefaultSource = "",
            DefaultProduct = "OIS",
            Discounting = "SELF",
            Ladders = new List<Ladder>
            {
                new()
                {
                    Name = "DI", Kind = "RATE", Aliases = new List<string> { "di", "cdi", "predi" },
                    Dcc = "BUS/252", FixingTicker = "BZDIOVRA Index",
                    DatedPattern = "OD{MY} Comdty",
                    Pillars = new List<PillarDef>
                    {
                        new() { Tenor = "1Y", Ticker = "BCSFLPDV" },
                        new() { Tenor = "2Y", Ticker = "BCSFPPDV" },
                        new() { Tenor = "5Y", Ticker = "BCSFSPDV" },
                        new() { Tenor = "10Y", Ticker = "BCSFXPDV" },
                    },
                },
            },
        };

        public static CurrencyConfig Mxn() => new()
        {
            Ccy = "MXN",
            Calendar = "MXN",
            SpotLag = 1,
            DefaultProduct = "OIS",
            Discounting = "OIS",
            Ois = new OisConfig
            {
                IndexName = "F-TIIE",
                IndexDcc = "ACT/360",
                FixedFreq = "Every28Days",
                FixedDcc = "ACT/360",
                Bdc = "Following",
                PayLag = 0,
                ShortZeroCouponUnder = "2P",
                Curve = new List<PillarDef>
                {
                    new() { Tenor = "3P", Type = "OIS", Ticker = "MPSWFC" },
                    new() { Tenor = "13P", Type = "OIS", Ticker = "MPSWF1A" },
                    new() { Tenor = "26P", Type = "OIS", Ticker = "MPSWF2B" },
                    new() { Tenor = "65P", Type = "OIS", Ticker = "MPSWF5E" },
                    new() { Tenor = "130P", Type = "OIS", Ticker = "MPSWF10J" },
                },
            },
        };
    }
}
