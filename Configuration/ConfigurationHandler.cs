using Microsoft.Extensions.Configuration;

namespace BigTyre.Phones.MusicOnHoldPlayer.Configuration
{
    internal static class ConfigurationHandler
    {
        private static IConfiguration BuildConfiguration()
        {
            var configBuilder = new ConfigurationBuilder();
            configBuilder.AddEnvironmentVariables();
            configBuilder.AddJsonFile("appsettings.json", optional: true);
#if DEBUG
            configBuilder.AddUserSecrets<Program>();
#endif

            var config = configBuilder.Build();

            return config;
        }

        public static AppSettings GetAppSettings()
        {
            var config = BuildConfiguration();

            var settings = new AppSettings();
            config.Bind(settings);

            return settings;
        }
    }
}
