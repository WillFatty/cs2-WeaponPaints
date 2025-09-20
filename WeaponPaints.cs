using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace WeaponPaints;

[MinimumApiVersion(338)]
public partial class WeaponPaints : BasePlugin, IPluginConfig<WeaponPaintsConfig>
{
	internal static WeaponPaints Instance { get; private set; } = new();

	public WeaponPaintsConfig Config { get; set; } = new();
    private static WeaponPaintsConfig _config { get; set; } = new();
    private static RefreshQueue? _refreshQueue;
    public override string ModuleAuthor => "Nereziel & daffyy";
	public override string ModuleDescription => "Skin, gloves, agents and knife selector, standalone and web-based";
	public override string ModuleName => "WeaponPaints";
	public override string ModuleVersion => "3.2a";

	public override void Load(bool hotReload)
	{
		// Hardcoded hotfix needs to be changed later (Not needed 17.09.2025)
		//if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
		//	Patch.PerformPatch("0F 85 ? ? ? ? 31 C0 B9 ? ? ? ? BA ? ? ? ? 66 0F EF C0 31 F6 31 FF 48 C7 45 ? ? ? ? ? 48 C7 45 ? ? ? ? ? 48 C7 45 ? ? ? ? ? 48 C7 45 ? ? ? ? ? 0F 29 45 ? 48 C7 45 ? ? ? ? ? C7 45 ? ? ? ? ? 66 89 45 ? E8 ? ? ? ? 41 89 C5 85 C0 0F 8E", "90 90 90 90 90 90");
		//else
		//	Patch.PerformPatch("74 ? 48 8D 0D ? ? ? ? FF 15 ? ? ? ? EB ? BA", "EB");
		
		Instance = this;

		if (hotReload)
		{
			OnMapStart(string.Empty);
			
			GPlayerWeaponsInfo.Clear();
			GPlayersKnife.Clear();
			GPlayersGlove.Clear();
			GPlayersAgent.Clear();
			GPlayersPin.Clear();
			GPlayersMusic.Clear();

			foreach (var player in Enumerable
				         .OfType<CCSPlayerController>(Utilities.GetPlayers().TakeWhile(_ => WeaponSync != null))
				         .Where(player => player.IsValid &&
					         !string.IsNullOrEmpty(player.IpAddress) && player is
						         { IsBot: false, Connected: PlayerConnectedState.PlayerConnected }))
			{
				var playerInfo = new PlayerInfo
				{
					UserId = player.UserId,
					Slot = player.Slot,
					Index = (int)player.Index,
					SteamId = player?.SteamID.ToString(),
					Name = player?.PlayerName,
					IpAddress = player?.IpAddress?.Split(":")[0]
				};

				_ = Task.Run(async () =>
				{
					if (WeaponSync != null) await WeaponSync.GetPlayerData(playerInfo);
				});
			}
		}

		Utility.LoadSkinsFromFile(ModuleDirectory + $"/data/skins_{_config.SkinsLanguage}.json", Logger);
		Utility.LoadGlovesFromFile(ModuleDirectory + $"/data/gloves_{_config.SkinsLanguage}.json", Logger);
		Utility.LoadAgentsFromFile(ModuleDirectory + $"/data/agents_{_config.SkinsLanguage}.json", Logger);
		Utility.LoadMusicFromFile(ModuleDirectory + $"/data/music_{_config.SkinsLanguage}.json", Logger);
		Utility.LoadPinsFromFile(ModuleDirectory + $"/data/collectibles_{_config.SkinsLanguage}.json", Logger);

		RegisterListeners();
	}

	public void OnConfigParsed(WeaponPaintsConfig config)
	{
		Config = config;
		_config = config;

		if (config.DatabaseHost.Length < 1 || config.DatabaseName.Length < 1 || config.DatabaseUser.Length < 1)
		{
			Logger.LogError("You need to setup Database credentials in \"configs/plugins/WeaponPaints/WeaponPaints.json\"!");
			Unload(false);
			return;
		}

		if (!File.Exists(Path.GetDirectoryName(Path.GetDirectoryName(ModuleDirectory)) + "/gamedata/weaponpaints.json"))
		{
			Logger.LogError("You need to upload \"weaponpaints.json\" to \"gamedata directory\"!");
			Unload(false);
			return;
		}
		
		var builder = new MySqlConnectionStringBuilder
		{
			Server = config.DatabaseHost,
			UserID = config.DatabaseUser,
			Password = config.DatabasePassword,
			Database = config.DatabaseName,
			Port = (uint)config.DatabasePort,
			Pooling = true,
			MaximumPoolSize = 640,
		};

		Database = new Database(builder.ConnectionString);

		_ = Utility.CheckDatabaseTables();
		_localizer = Localizer;

		Utility.Config = config;
		Utility.ShowAd(ModuleVersion);
		Task.Run(async () => await Utility.CheckVersion(ModuleVersion, Logger));
		
		// Initialize refresh queue for auto-refresh functionality
		_refreshQueue = new RefreshQueue(Database);
		Utility.Log("Auto-refresh queue initialized - checking database for refresh requests");
	}

	public override void OnAllPluginsLoaded(bool hotReload)
	{
		try
		{
			MenuApi = MenuCapability.Get();
			
			if (Config.Additional.KnifeEnabled)
				SetupKnifeMenu();
			if (Config.Additional.SkinEnabled)
				SetupSkinsMenu();
			if (Config.Additional.GloveEnabled)
				SetupGlovesMenu();
			if (Config.Additional.AgentEnabled)
				SetupAgentsMenu();
			if (Config.Additional.MusicEnabled)
				SetupMusicMenu();
			if (Config.Additional.PinsEnabled)
				SetupPinsMenu();
		
			RegisterCommands();
		}
		catch (Exception)
		{
			MenuApi = null;
			Logger.LogError("Error while loading required plugins");
			throw;
		}
	}

	internal void ApplyInspectData(CCSPlayerController player, InspectWeaponData weaponData, PlayerInfo playerInfo)
	{
		try
		{
			// Get or create player weapons info
			var playerSkins = GPlayerWeaponsInfo.GetOrAdd(player.Slot, _ => new ConcurrentDictionary<CsTeam, ConcurrentDictionary<int, WeaponInfo>>());
			
			// Determine teams to apply to
			var teamsToCheck = player.TeamNum < 2 
				? new[] { CsTeam.Terrorist, CsTeam.CounterTerrorist } 
				: [player.Team];

			foreach (var team in teamsToCheck)
			{
				// Ensure there's an entry for the team in playerSkins
				var teamWeapons = playerSkins.GetOrAdd(team, _ => new ConcurrentDictionary<int, WeaponInfo>());

				// Create or update the weapon info
				var weaponInfo = teamWeapons.GetOrAdd(weaponData.WeaponDefIndex, _ => new WeaponInfo());
				
				// Update weapon properties
				weaponInfo.Paint = weaponData.PaintId;
				weaponInfo.Wear = weaponData.Wear;
				weaponInfo.Seed = weaponData.Seed;
				weaponInfo.StatTrak = weaponData.StatTrak > 0;
				weaponInfo.StatTrakCount = weaponData.StatTrakCount;
				weaponInfo.Nametag = weaponData.NameTag;

				// Apply stickers
				for (int i = 0; i < Math.Min(weaponData.Stickers.Count, 5); i++)
				{
					var sticker = weaponData.Stickers[i];
					weaponInfo.SetSticker(i, (int)sticker.Id, (int)sticker.Schema, sticker.OffsetX, sticker.OffsetY, sticker.Wear, sticker.Scale, sticker.Rotation);
				}

				// Apply keychain
				if (weaponData.Keychain.Id > 0)
				{
					weaponInfo.SetKeychain((int)weaponData.Keychain.Id, weaponData.Keychain.OffsetX, weaponData.Keychain.OffsetY, weaponData.Keychain.OffsetZ, (int)weaponData.Keychain.Seed);
				}
			}

			// Refresh weapons to apply the changes
			RefreshWeapons(player);
		}
		catch (Exception ex)
		{
			Utility.Log($"Error applying inspect data: {ex.Message}");
		}
	}
	
	public override void Unload(bool hotReload)
	{
		// Cleanup refresh queue
		_refreshQueue?.Dispose();
		_refreshQueue = null;
		
		base.Unload(hotReload);
	}
}
