namespace BytexDigital.Steam.ContentDelivery.Enumerations
{
    public enum ManifestFileFlag
    {
        UserConfig = 1,
        VersionedUserConfig = 2,
        Encrypted = 4,
        ReadOnly = 8,
        Hidden = 16,
        Executable = 32,
        Directory = 64,
        CustomExecutable = 128,
        InstallScript = 256,
    }
}
