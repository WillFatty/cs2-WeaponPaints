using CounterStrikeSharp.API;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using System.Threading;

namespace WeaponPaints
{
	public class Database(string dbConnectionString)
	{
		private readonly SemaphoreSlim _connectionSemaphore = new SemaphoreSlim(5, 10); // Limit concurrent connections
		private readonly int _maxRetries = 3;
		private readonly TimeSpan _retryDelay = TimeSpan.FromSeconds(1);
        
		public async Task<MySqlConnection> GetConnectionAsync()
		{
			// Wait for a connection slot to become available
			await _connectionSemaphore.WaitAsync();
			
			try
			{
				for (int attempt = 1; attempt <= _maxRetries; attempt++)
				{
					try
					{
						var connection = new MySqlConnection(dbConnectionString);

                        await connection.OpenAsync();
						return connection;
					}
					catch (Exception ex)
					{
						if (attempt == _maxRetries)
						{
							WeaponPaints.Instance.Logger.LogError($"Unable to connect to database after {attempt} attempts: {ex.Message}");
							throw;
						}
						
						WeaponPaints.Instance.Logger.LogWarning($"Database connection attempt {attempt} failed: {ex.Message}. Retrying...");
						await Task.Delay(_retryDelay);
					}
				}
				
				// This should never be reached due to the throw in the catch block
				throw new Exception("Failed to establish database connection after retries");
			}
			finally
			{
				_connectionSemaphore.Release();
			}
		}

        /// <summary>
        /// Monitors for changes in the player's data and refreshes their skins if needed
        /// </summary>
        /// <param name="steamId">The SteamID of the player that made changes</param>
        public void RefreshPlayerWeaponsIfChanged(string steamId)
        {
            if (string.IsNullOrEmpty(steamId) || WeaponPaints.Instance == null)
                return;

            try
            {
                // Run this on the main game thread via Server.NextFrame to ensure thread safety
                Server.NextFrame(() => {
                    // Refresh the player's skins directly
                    WeaponPaints.Instance.RefreshPlayerSkinsByDatabase(steamId);
                });
                
                // Log the refresh request
                WeaponPaints.Instance.Logger.LogInformation($"Refresh request queued for player {steamId}");
            }
            catch (Exception ex)
            {
                WeaponPaints.Instance.Logger.LogError($"Error monitoring database changes: {ex.Message}");
            }
        }

        /// <summary>
        /// Execute a database query with automatic retry logic
        /// </summary>
        public async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> operation)
        {
            for (int attempt = 1; attempt <= _maxRetries; attempt++)
            {
                try
                {
                    return await operation();
                }
                catch (MySqlException ex) when (ex.IsTransient)
                {
                    // Only retry on transient errors
                    if (attempt == _maxRetries)
                    {
                        WeaponPaints.Instance.Logger.LogError($"Database operation failed after {attempt} attempts: {ex.Message}");
                        throw;
                    }
                    
                    WeaponPaints.Instance.Logger.LogWarning($"Database operation attempt {attempt} failed: {ex.Message}. Retrying...");
                    await Task.Delay(_retryDelay * attempt); // Exponential backoff
                }
            }
            
            // This should never be reached
            throw new Exception("Failed to execute database operation after retries");
        }
    }
}