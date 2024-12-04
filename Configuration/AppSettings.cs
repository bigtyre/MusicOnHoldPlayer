namespace BigTyre.Phones.MusicOnHoldPlayer.Configuration
{
    internal class AppSettings
    {
        public string? MediaDirectory { get; set; }
        public string? PBXIPAddress { get; set; }
        public uint? PBXPort { get; set; }
        public string? PBXAuthenticationRealm { get; set; }
        public List<SIPAccount> Accounts { get; set; } = [];
        public uint? SIPRegistrationExpirySeconds { get; set; }
    }
}
