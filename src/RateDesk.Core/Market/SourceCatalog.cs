namespace RateDesk.Core.Market
{
    /// <summary>Candidate Bloomberg contributor mnemonics probed live per currency to populate the
    /// source dropdown. "" = default composite. Discovery keeps only those that return a price.</summary>
    public static class SourceCatalog
    {
        public static readonly string[] Candidates =
        {
            "", "BGN", "CMPN", "CMPL", "CMPT", "BVAL",
            "BARX", "UBSW", "RBCX", "HSBC", "CITX",
            "BMOD", "NABZ", "ANZB", "WSTP",
            "TRAD", "ICPL", "TTKL", "GFIS",
        };
    }
}
