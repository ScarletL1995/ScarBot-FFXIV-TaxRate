using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using ScarBot;
using ScarBot_FFXIV_TaxRate;

namespace ScarBot.FFXIVTaxRate
{
    public class FFXIVTaxRatePlugin : IScarBotPlugin
    {
        public string Name => "FFXIV Tax Rate Plugin";

        [PluginService] IDatabaseManager databaseManager { get; set; } = null!;
        [PluginService] DiscordSocketClient Client { get; set; } = null!;
        [PluginService] ILogger Logger { get; set; } = null!;

        private List<string> validServers = new List<string>();

        public void Initialize() { }

        public void RegisterCommands(ICommandRegistrar handler)
        {
            handler.RegisterCommand(new Command
            {
                Name = "taxrate",
                ShortDescription = "Shows FFXIV market board tax rates",
                LongDescription = "Fetches current Final Fantasy XIV market board tax rates and shows the best market locations.",
                Category = "FFXIV",
                Aliases = new List<string>(),
                Function = async (CommandContext ctx) => await ExecuteTaxRate(ctx),
                Parameters = new List<CommandOption>
                {
                    new CommandOption
                    {
                        Name = "server",
                        Description = "The server (world) name (ex: Exodus, Leviathan) or 'all' to display all servers",
                        Type = ApplicationCommandOptionType.String,
                        Required = false
                    }
                }
            });
            handler.RegisterCommand(new Command
            {
                Name = "setup-taxrate-channel",
                ShortDescription = "Sets up a read-only channel for FFXIV tax rates",
                LongDescription = "Creates or configures a channel to display FFXIV tax rates for a specified server. Only administrators can use this command.",
                Category = "FFXIV",
                Aliases = new List<string>(),
                Function = async (CommandContext ctx) => await SetupTaxRateChannel(ctx),
                Parameters = new List<CommandOption>
                {
                    new CommandOption
                    {
                        Name = "server",
                        Description = "The server (world) name (e.g., Exodus, Leviathan)",
                        Type = ApplicationCommandOptionType.String,
                        Required = true
                    },
                    new CommandOption
                    {
                        Name = "channel",
                        Description = "The channel ID to send the tax rate message to (optional, defaults to current channel)",
                        Type = ApplicationCommandOptionType.String,
                        Required = false
                    }
                }
            });

            ConfigLoader config = ConfigLoader.Load("database.json");
            databaseManager.CreateUser(config.DatabaseUsername, config.DatabasePassword);
            databaseManager.Login(config.DatabaseUsername, config.DatabasePassword);

            List<string> tables = databaseManager.ViewAllTables();
            if(!tables.Contains("FFXIVTaxSettings"))
                databaseManager.CreateTable("FFXIVTaxSettings", new Dictionary<string, string> { { "GuildId", "BIGINT UNSIGNED" }, { "Server", "VARCHAR(100)" }, { "ChannelId", "BIGINT UNSIGNED" }, { "MessageId", "BIGINT UNSIGNED" } });
            Task.Run(async () => await ScheduleWeeklyTaxUpdate());
        }

        private async Task ExecuteTaxRate(CommandContext ctx)
        {
            string server = ctx.SlashCommand?.Data.Options.FirstOrDefault(x => x.Name == "server")?.Value?.ToString()?.ToLower() ?? string.Empty;
            server = server.Replace("'", "");
            if (string.IsNullOrEmpty(server))
            {
                await ctx.RespondInfo("Please specify a server. Usage: `/taxrate <server_name>`.", true);
                return;
            }
            if (server == "all")
            {
                await DisplayAllServersTaxRates(ctx);
                return;
            }
            if (!validServers.Contains(server))
            {
                await ctx.RespondInfo($"Invalid server '{server}'. Please specify a valid server.", true);
                return;
            }
            Embed embed = await GenerateTaxEmbed(server);
            await ctx.Respond("", embed, false);
        }

        private async Task SetupTaxRateChannel(CommandContext ctx)
        {
            ulong guildId = (ctx.SlashCommand?.GuildId ?? (ctx.Message?.Channel as SocketGuildChannel)?.Guild.Id) ?? 0;
            string server = ctx.SlashCommand?.Data.Options.FirstOrDefault(x => x.Name == "server")?.Value?.ToString()?.ToLower() ?? string.Empty;
            string? channelIdRaw = ctx.SlashCommand?.Data.Options.FirstOrDefault(x => x.Name == "channel")?.Value?.ToString();
            ulong channelId = ctx.SlashCommand?.Channel?.Id ?? ctx.Message?.Channel.Id ?? 0;
            if (string.IsNullOrWhiteSpace(server))
            {
                DataTable? result = await databaseManager.QueryAsync("FFXIVTaxSettings", new Dictionary<string, object> { { "GuildId", guildId } });
                string currentServer = result?.Rows.Count > 0 ? result.Rows[0]["Server"].ToString() ?? "Not set" : "Not set";
                string currentChannel = result?.Rows.Count > 0 ? $"<#{result.Rows[0]["ChannelId"]}>" : "Not set";
                await ctx.RespondInfo($"To set the taxrate channel: `/setup-taxrate-channel server:<server> [channel:<channel>]`\nCurrent settings:\nServer: {currentServer}\nChannel: {currentChannel}", true);
                return;
            }
            if (!validServers.Contains(server))
            {
                await ctx.RespondInfo($"Invalid server '{server}'. Please specify a valid server.", true);
                return;
            }
            if (!ulong.TryParse(channelIdRaw, out ulong parsedChannelId))
                parsedChannelId = channelId;
            IMessageChannel? resolvedChannel = Client.GetChannel(parsedChannelId) as IMessageChannel;
            if (resolvedChannel == null)
            {
                await ctx.RespondInfo("Invalid channel ID.", true);
                return;
            }
            Embed embed = await GenerateTaxEmbed(server);
            IUserMessage msg = await resolvedChannel.SendMessageAsync(embed: embed);
            await databaseManager.InsertAsync("FFXIVTaxSettings", new Dictionary<string, object> { { "GuildId", guildId }, { "Server", server }, { "ChannelId", parsedChannelId }, { "MessageId", msg.Id } }, true);
            await ctx.RespondSuccess($"Tax rate updates set for **{server}** in <#{parsedChannelId}> for message: <{msg.Id}>");
            SocketGuild? guild = Client.GetGuild(guildId);
            if (guild?.GetChannel(parsedChannelId) is SocketTextChannel textChannel)
            {
                SocketRole? everyoneRole = guild.EveryoneRole;
                OverwritePermissions denySend = new OverwritePermissions(sendMessages: PermValue.Deny);
                await textChannel.AddPermissionOverwriteAsync(everyoneRole, denySend);
            }
        }

        private async Task ScheduleWeeklyTaxUpdate()
        {
            while (true)
            {
                DateTimeOffset next = TaxRateHelper.GetTaxResetTimeUtc().AddMinutes(10);
                TimeSpan delay = next - DateTimeOffset.UtcNow;
                if (delay.TotalMilliseconds > 0)
                    await Task.Delay(delay);
                await LoadValidServers();
                foreach (SocketGuild guild in Client.Guilds)
                {
                    DataTable? result = await databaseManager.QueryAsync("FFXIVTaxSettings", new Dictionary<string, object> { { "GuildId", guild.Id } });
                    if (result == null || result.Rows.Count == 0)
                        continue;

                    DataRow row = result.Rows[0];
                    string? server = row["Server"]?.ToString();
                    ulong channelId = row["ChannelId"] is ulong cid ? cid : ulong.TryParse(row["ChannelId"]?.ToString(), out cid) ? cid : 0;
                    ulong messageId = row["MessageId"] is ulong mid ? mid : ulong.TryParse(row["MessageId"]?.ToString(), out mid) ? mid : 0;

                    if (string.IsNullOrWhiteSpace(server) || channelId == 0 || messageId == 0)
                        continue;

                    Embed embed = await GenerateTaxEmbed(server);
                    if (Client.GetChannel(channelId) is IMessageChannel chan)
                    {
                        IUserMessage? msg = await chan.GetMessageAsync(messageId) as IUserMessage;
                        if (msg != null)
                            await msg.ModifyAsync(m => m.Embed = embed);
                    }
                }
            }
        }

        private async Task LoadValidServers()
        {
            using HttpClient client = new HttpClient();
            string sresponse = await client.GetStringAsync("https://universalis.app/api/v2/worlds");
            List<World>? worlds = JsonSerializer.Deserialize<List<World>>(sresponse);
            validServers = worlds?.Select(w => w.Name.ToLowerInvariant()).ToList() ?? new List<string>();
        }

        private async Task<Embed> GenerateTaxEmbed(string server)
        {
            Dictionary<string, int> rates = await TaxRateHelper.GetTaxRates(server);
            double unixSeconds = TaxRateHelper.UnixTime(TaxRateHelper.GetTaxResetTimeUtc());
            int lowestRate = rates.Values.Min();
            int occurrences = rates.Values.Count(x => x == lowestRate);
            string msg = $"Current Tax Rates until <t:{(long)unixSeconds}:F> are:\n";
            foreach ((string location, int rate) in rates)
            {
                string reduced = rate < 5 ? " (Reduced)" : "";
                msg += $"- {location}: {rate}%{reduced}\n";
            }
            msg += $"\nBest location{(occurrences > 1 ? "s" : "")} to place retainers:\n";
            Dictionary<string, string> retainerLocations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Limsa Lominsa", "Frydwyb (Limsa Lominsa Lower Decks 8.3,11.5)" },
            { "Gridania", "Parnell (Old Gridania 14.6,9.3)" },
            { "Ul'dah", "Chachabi (Ul'dah - Steps of Thal 13.3,9.7)" },
            { "Ishgard", "Prunilla (The Pillars 8.1,10.9)" },
            { "Kugane", "Kazashi (Kugane 11.6,12.1)" },
            { "Crystarium", "Misfrith (The Crystarium 10.4,13.1)" },
            { "Old Sharlayan", "Tanine (Old Sharlayan 12.6,10.8)" },
            { "Tuliyollal", "Wuk Ty'ukuk (Tuliyollal 12.7,13.1)" }
        };
            foreach ((string location, int rate) in rates)
                if (rate == lowestRate && retainerLocations.TryGetValue(location, out string? retainer))
                    msg += $"- {retainer}\n";
            return new EmbedBuilder().WithTitle($"FFXIV Market Tax Rates - {CultureInfo.CurrentCulture.TextInfo.ToTitleCase(server)}").WithDescription(msg).WithColor(Color.DarkBlue).Build();
        }

        private static async Task DisplayAllServersTaxRates(CommandContext ctx)
        {
            try
            {
                using HttpClient client = new HttpClient();
                string sresponse = await client.GetStringAsync("https://universalis.app/api/v2/worlds");
                List<World>? worlds = JsonSerializer.Deserialize<List<World>>(sresponse);
                List<string> allServers = worlds?.Select(w => w.Name.ToLowerInvariant()).ToList() ?? new List<string>();

                if (allServers.Count == 0)
                {
                    await ctx.RespondInfo("No servers found.", true);
                    return;
                }

                int pageSize = 10;
                int totalPages = (int)Math.Ceiling(allServers.Count / (double)pageSize);

                for (int page = 0; page < totalPages; page++)
                {
                    var serversOnPage = allServers.Skip(page * pageSize).Take(pageSize).ToList();
                    string msg = $"Current Tax Rates for all servers (Page {page + 1}/{totalPages}):\n";

                    IEnumerable<Task<string>> tasks = serversOnPage.Select(async world =>
                    {
                        Dictionary<string, int> rates = await TaxRateHelper.GetTaxRates(world);
                        if (rates.Count > 0)
                        {
                            string serverMsg = $"\n**{CultureInfo.CurrentCulture.TextInfo.ToTitleCase(world)}**:\n";
                            foreach ((string location, int rate) in rates)
                            {
                                string reduced = rate < 5 ? " (Reduced)" : "";
                                serverMsg += $"- {location}: {rate}%{reduced}\n";
                            }
                            return serverMsg;
                        }
                        else
                        {
                            return $"\n**{CultureInfo.CurrentCulture.TextInfo.ToTitleCase(world)}**: No tax rate data available.\n";
                        }
                    });
                    string[] results = await Task.WhenAll(tasks);
                    msg += string.Join("", results);
                    Embed embed = new EmbedBuilder().WithTitle("FFXIV Market Tax Rates - All Servers").WithDescription(msg).WithColor(Color.DarkBlue).Build();
                    await ctx.Respond(null, embed, false);
                }
            }
            catch (Exception ex)
            {
                await ctx.RespondInfo($"An error occurred while fetching tax rates for all servers: {ex.Message}", true);
            }
        }
    }

    internal static class TaxRateHelper
    {
        public static async Task<Dictionary<string, int>> GetTaxRates(string server)
        {
            using HttpClient client = new HttpClient();
            string response = await client.GetStringAsync($"https://universalis.app/api/tax-rates?world={Uri.EscapeDataString(server)}");
            Dictionary<string, int>? rates = JsonSerializer.Deserialize<Dictionary<string, int>>(response);
            return rates ?? new Dictionary<string, int>();
        }

        public static DateTimeOffset GetTaxResetTimeUtc()
        {
            DateTime utcNow = DateTime.UtcNow;
            int daysUntilSaturday = ((int)DayOfWeek.Saturday - (int)utcNow.DayOfWeek + 7) % 7;
            if (daysUntilSaturday == 0 && utcNow.Hour >= 7) daysUntilSaturday = 7;
            return utcNow.Date.AddDays(daysUntilSaturday).AddHours(7);
        }

        public static double UnixTime(DateTimeOffset time) => Math.Floor((time.ToUniversalTime() - DateTime.UnixEpoch).TotalSeconds);
    }

    public class World
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;
    }
}