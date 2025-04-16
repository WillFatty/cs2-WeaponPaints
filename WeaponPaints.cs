using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Timers;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using Dapper;
using System.Threading;

namespace WeaponPaints;

[MinimumApiVersion(276)]
public partial class WeaponPaints : BasePlugin, IPluginConfig<WeaponPaintsConfig>
{
	internal static WeaponPaints Instance { get; private set; } = new();

	public WeaponPaintsConfig Config { get; set; } = new();
    private static WeaponPaintsConfig _config { get; set; } = new();
    public override string ModuleAuthor => "Nereziel & daffyy";
	public override string ModuleDescription => "Skin, gloves, agents and knife selector, standalone and web-based";
	public override string ModuleName => "WeaponPaints";
	public override string ModuleVersion => "3.1c";

	public override void Load(bool hotReload)
	{
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
		
		// Start the refresh queue checker timer - check every 500ms
		AddTimer(0.5f, CheckRefreshQueue, TimerFlags.REPEAT);
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
	
	/// <summary>
	/// Method to refresh a specific player's skins after a database update
	/// </summary>
	/// <param name="steamId">The SteamID of the player</param>
	public void RefreshPlayerSkinsByDatabase(string steamId)
	{
		if (WeaponSync == null || string.IsNullOrEmpty(steamId))
			return;
			
		// Use the WeaponSync to refresh the player by SteamID
		// This will run on the main thread via Server.NextFrame in the RefreshPlayerBySteamId method
		bool refreshed = WeaponSync.RefreshPlayerBySteamId(steamId);
		
		if (refreshed)
		{
			Logger.LogInformation($"Refreshed skins for player with SteamID {steamId} after database update");
		}
	}

	/// <summary>
	/// Periodically checks the refresh queue table for any pending refreshes (every 500ms)
	/// </summary>
	private async void CheckRefreshQueue()
	{
		if (WeaponSync == null || Database == null)
			return;
			
		try
		{
			// Use a semaphore to prevent overlapping refresh queue checks
			using var semaphore = new SemaphoreSlim(1, 1);
			
			// Don't wait if already processing to prevent backup with faster refresh rate
			if (!await semaphore.WaitAsync(0)) 
				return;
				
			try
			{
				await using var connection = await Database.GetConnectionAsync();
				
				// Check if the refresh queue table exists (only check on first run)
				if (!_refreshQueueTableVerified)
				{
					var tableExists = await connection.ExecuteScalarAsync<int>(
						"SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'wp_refresh_queue'"
					);
					
					if (tableExists == 0)
					{
						// Table doesn't exist yet, skip for now
						_refreshQueueTableVerified = false;
						return;
					}
					
					_refreshQueueTableVerified = true;
				}
					
				// Query for pending refreshes - process more at once since we check more frequently
				var refreshes = await connection.QueryAsync<string>(
					"SELECT `steamid` FROM `wp_refresh_queue` ORDER BY `refresh_time` ASC LIMIT 5"
				);
				
				if (refreshes == null || !refreshes.Any())
					return;
					
				// Get all steamIDs for cleanup
				var steamIdsToProcess = refreshes.Where(id => !string.IsNullOrEmpty(id)).ToList();
				if (steamIdsToProcess.Count == 0)
					return;
				
				// Process immediately on main thread
				Server.NextFrame(() => {
					foreach (var steamId in steamIdsToProcess)
					{
						// Refresh the player's skins
						RefreshPlayerSkinsByDatabase(steamId);
					}
				});
				
				// Delete the processed entries from the queue immediately
				if (steamIdsToProcess.Count > 0)
				{
					try
					{
						// Use efficient parameterized query for bulk delete
						string placeholders = string.Join(",", steamIdsToProcess.Select((_, i) => $"@p{i}"));
						var parameters = new DynamicParameters();
						
						for (int i = 0; i < steamIdsToProcess.Count; i++)
						{
							parameters.Add($"p{i}", steamIdsToProcess[i]);
						}
						
						string query = $"DELETE FROM `wp_refresh_queue` WHERE `steamid` IN ({placeholders})";
						await connection.ExecuteAsync(query, parameters);
					}
					catch (Exception ex)
					{
						Logger.LogError($"Error cleaning up refresh queue: {ex.Message}");
					}
				}
			}
			finally
			{
				semaphore.Release();
			}
		}
		catch (Exception ex)
		{
			Logger.LogError($"Error checking refresh queue: {ex.Message}");
		}
	}

	// Flag to avoid rechecking table existence on every call
	private bool _refreshQueueTableVerified = false;
}