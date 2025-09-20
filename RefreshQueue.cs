using System.Collections.Concurrent;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using MySqlConnector;
using Dapper;

namespace WeaponPaints;

public class RefreshQueue
{
    private readonly Timer _refreshCheckTimer;
    private readonly ConcurrentDictionary<string, DateTime> _lastRefreshTime;
    private readonly Database _database;
    
    public RefreshQueue(Database database)
    {
        _database = database;
        _lastRefreshTime = new ConcurrentDictionary<string, DateTime>();
        
        
        // Check for refresh requests every 2 seconds
        _refreshCheckTimer = new Timer(CheckRefreshQueue, null, TimeSpan.Zero, TimeSpan.FromSeconds(2));
    }
    
    private async void CheckRefreshQueue(object? state)
    {
        try
        {
            await using var connection = await _database.GetConnectionAsync();
            
            // Get unprocessed refresh requests
            var requests = await connection.QueryAsync<RefreshRequest>(
                "SELECT id, steamid, refresh_type FROM wp_player_refresh_queue WHERE processed = 0 ORDER BY created_at ASC LIMIT 100");
            
            if (requests.Any())
            {
                var requestList = requests.ToList();
                
                // Mark as processed
                var ids = requestList.Select(r => r.Id).ToArray();
                if (ids.Length > 0)
                {
                    var placeholders = string.Join(",", ids.Select((_, i) => $"@id{i}"));
                    var parameters = ids.Select((id, i) => new { Name = $"@id{i}", Value = id }).ToList();
                    var paramDict = parameters.ToDictionary(p => p.Name, p => (object)p.Value);
                    
                    await connection.ExecuteAsync(
                        $"UPDATE wp_player_refresh_queue SET processed = 1, processed_at = NOW() WHERE id IN ({placeholders})",
                        paramDict);
                }
                
                // Process each request on the main thread
                foreach (var request in requestList)
                {
                    // Schedule on main thread to avoid "non-main thread" errors
                    Server.NextFrame(() => ProcessRefreshRequest(request));
                }
            }
        }
        catch (Exception)
        {
            // Silent fail - refresh queue check failed
        }
    }
    
    private void ProcessRefreshRequest(RefreshRequest request)
    {
        try
        {
            // Find the player by SteamID
            var player = Utilities.GetPlayers()
                .FirstOrDefault(p => p.IsValid && 
                              !p.IsBot && 
                              p.Connected == PlayerConnectedState.PlayerConnected &&
                              p.SteamID.ToString() == request.SteamId);
            
            if (player == null)
            {
                return;
            }
            
            // Check if we recently refreshed this player (prevent spam)
            if (_lastRefreshTime.TryGetValue(request.SteamId, out var lastRefresh))
            {
                if (DateTime.UtcNow - lastRefresh < TimeSpan.FromSeconds(5))
                {
                    return;
                }
            }
            
            // Create player info
            var playerInfo = new PlayerInfo
            {
                UserId = player.UserId,
                Slot = player.Slot,
                Index = (int)player.Index,
                SteamId = player.SteamID.ToString(),
                Name = player.PlayerName,
                IpAddress = player.IpAddress?.Split(":")[0]
            };
            
            // Process the refresh based on type
            // Use Task.Run to handle async database operations properly
            _ = Task.Run(async () =>
            {
                try
                {
                    // Load player data from database
                    if (WeaponPaints.WeaponSync != null)
                    {
                        await WeaponPaints.WeaponSync.GetPlayerData(playerInfo);
                        
                        // Schedule the actual refresh operations on the main thread
                        Server.NextFrame(() =>
                        {
                            try
                            {
                                // Always do a complete refresh like !wp command does
                                // This ensures all player data is properly applied
                                WeaponPaints.Instance.GivePlayerGloves(player);
                                WeaponPaints.Instance.RefreshWeapons(player);
                                WeaponPaints.GivePlayerAgent(player);
                                WeaponPaints.GivePlayerMusicKit(player);
                                WeaponPaints.Instance.AddTimer(0.15f, () => WeaponPaints.GivePlayerPin(player));
                            }
                            catch (Exception)
                            {
                                // Silent fail - refresh operation failed
                            }
                        });
                    }
                }
                catch (Exception)
                {
                    // Silent fail - player data loading failed
                }
            });
            
            // Update last refresh time
            _lastRefreshTime.AddOrUpdate(request.SteamId, DateTime.UtcNow, (key, oldValue) => DateTime.UtcNow);
            
        }
        catch (Exception)
        {
            // Silent fail - refresh request processing failed
        }
    }
    
    public void Dispose()
    {
        _refreshCheckTimer?.Dispose();
    }
}

public class RefreshRequest
{
    public int Id { get; set; }
    public string SteamId { get; set; } = string.Empty;
    public string RefreshType { get; set; } = string.Empty;
}
