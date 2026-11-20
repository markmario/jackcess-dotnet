namespace JackcessDotNet.AccessTests.Support
{
    /// <summary>
    /// The connection string the tests open ACE with: Microsoft.ACE.OLEDB.12.0, which both the Access Database Engine
    /// 2016 redistributable and Access itself register, unless <c>JACKCESS_ACE_PROVIDER</c> names another
    /// (Microsoft.ACE.OLEDB.16.0, say).
    /// </summary>
    internal static class AceVerification
    {
        public static string ConnectionString(string dataSource)
        {
            return string.Format(
                @"Provider={0};Data Source={1};Persist Security Info=False;OLE DB Services=-4",
                ProviderName, dataSource);
        }

        public static string ProviderName
        {
            get
            {
                var overridden = System.Environment.GetEnvironmentVariable("JACKCESS_ACE_PROVIDER");
                return string.IsNullOrWhiteSpace(overridden) ? "Microsoft.ACE.OLEDB.12.0" : overridden;
            }
        }
    }
}
