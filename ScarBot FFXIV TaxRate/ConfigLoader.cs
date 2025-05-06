using System.Text.Json;


namespace ScarBot_FFXIV_TaxRate
{
    internal class ConfigLoader
    {
        public string DatabaseUsername { get; set; } = "";
        public string DatabasePassword { get; set; } = "";

        public static ConfigLoader Load(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException($"Configuration file not found: {path}");

            string json = File.ReadAllText(path);
            ConfigLoader? config = JsonSerializer.Deserialize<ConfigLoader>(json);

            if (config == null || string.IsNullOrWhiteSpace(config.DatabaseUsername) || string.IsNullOrWhiteSpace(config.DatabasePassword))
                throw new InvalidDataException("Configuration file is invalid or missing required values.");

            return config;
        }
    }
