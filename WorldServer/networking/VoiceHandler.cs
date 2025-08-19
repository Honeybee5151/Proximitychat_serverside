//777592
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Shared.database.account;
using WorldServer.core;
using WorldServer.networking.packets.outgoing;

namespace WorldServer.networking
{
    // UDP Voice Packet Types
    public class UdpVoiceData
    {
        public string PlayerId { get; set; }
        public byte[] OpusAudioData { get; set; }
        public float Volume { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class UdpAuthRequest
    {
        public string PlayerId { get; set; }
        public string VoiceId { get; set; }
        public string Command { get; set; } = "AUTH";
    }

    public class UdpPriorityCommand
    {
        public string PlayerId { get; set; }
        public string SettingType { get; set; }
        public string Value { get; set; }
        public string Command { get; set; } = "PRIORITY";
    }

    public class VoiceHandler
    {
        private readonly GameServer gameServer;
        private const float PROXIMITY_RANGE = 15.0f;
        private readonly ConcurrentDictionary<int, VoicePrioritySettings> worldPrioritySettings = new();
        
        public VoiceHandler(GameServer server)
        {
            gameServer = server;
        }
        
        public bool ArePlayersVoiceIgnored(string speakerId, string listenerId)
        {
            try
            {
                var speakerAccount = gameServer.Database.GetAccount(int.Parse(speakerId));
                var listenerAccount = gameServer.Database.GetAccount(int.Parse(listenerId));
        
                if (speakerAccount == null || listenerAccount == null)
                    return false;

                bool listenerIgnoresSpeaker = listenerAccount.IgnoreList.Contains(speakerAccount.AccountId);
                bool speakerIgnoresListener = speakerAccount.IgnoreList.Contains(listenerAccount.AccountId);
        
                return listenerIgnoresSpeaker || speakerIgnoresListener;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error checking voice ignore status: {ex.Message}");
                return false;
            }
        }
        
        public PlayerPosition GetPlayerPosition(string playerId)
        {
            try
            {
                var clients = gameServer.ConnectionManager.Clients;
                foreach (var clientPair in clients)
                {
                    var client = clientPair.Key;
                    if (client.Account?.AccountId.ToString() == playerId || 
                        client.Player?.AccountId.ToString() == playerId)
                    {
                        if (client.Player != null && client.Player.World != null)
                        {
                            return new PlayerPosition
                            {
                                X = client.Player.X,
                                Y = client.Player.Y,
                                WorldId = client.Player.World.Id
                            };
                        }
                    }
                }
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting player position: {ex.Message}");
                return null;
            }
        }
        
        public VoicePlayerInfo[] GetPlayersInRange(float speakerX, float speakerY, float range)
        {
            try
            {
                var nearbyPlayers = new List<VoicePlayerInfo>();
                var clients = gameServer.ConnectionManager.Clients;
                
                foreach (var clientPair in clients)
                {
                    var client = clientPair.Key;
                    if (client.Player != null && client.Player.World != null)
                    {
                        float distance = CalculateDistance(speakerX, speakerY, client.Player.X, client.Player.Y);
                        
                        if (distance <= range)
                        {
                            nearbyPlayers.Add(new VoicePlayerInfo
                            {
                                PlayerId = client.Player.AccountId.ToString(),
                                Client = client,
                                Distance = distance
                            });
                        }
                    }
                }
                
                return nearbyPlayers.ToArray();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error finding nearby players: {ex.Message}");
                return new VoicePlayerInfo[0];
            }
        }
        
        public VoicePrioritySettings GetPrioritySettings(int worldId)
        {
            return worldPrioritySettings.GetOrAdd(worldId, _ => new VoicePrioritySettings());
        }

        public bool ShouldActivatePrioritySystem(int worldId, int nearbyPlayerCount)
        {
            var settings = GetPrioritySettings(worldId);
            if (!settings.EnablePriority) return false;
            
            return nearbyPlayerCount >= settings.ActivationThreshold;
        }

        public bool HasVoicePriority(string playerId, string listenerId, VoicePrioritySettings settings)
        {
            try
            {
                int playerAccountId = int.Parse(playerId);
                int listenerAccountId = int.Parse(listenerId);
        
                if (settings.HasManualPriority(playerAccountId))
                    return true;

                var playerAccount = gameServer.Database.GetAccount(playerAccountId);
                var listenerAccount = gameServer.Database.GetAccount(listenerAccountId);

                if (playerAccount == null || listenerAccount == null)
                    return false;

                if (settings.GuildMembersGetPriority && playerAccount.GuildId > 0)
                {
                    if (playerAccount.GuildId == listenerAccount.GuildId)
                        return true;
                }

                if (settings.LockedPlayersGetPriority && listenerAccount.LockList.Contains(playerAccountId))
                {
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error checking voice priority: {ex.Message}");
                return false;
            }
        }
        
        private float CalculateDistance(float x1, float y1, float x2, float y2)
        {
            float dx = x1 - x2;
            float dy = y1 - y2;
            return (float)Math.Sqrt(dx * dx + dy * dy);
        }
    }

    public class UdpVoiceHandler
    {
        private readonly GameServer gameServer;
        private readonly VoiceHandler voiceUtils;
        private UdpClient udpServer;
        private readonly ConcurrentDictionary<string, IPEndPoint> playerUdpEndpoints = new();
        private readonly ConcurrentDictionary<string, DateTime> lastUdpActivity = new();
        private readonly ConcurrentDictionary<string, bool> authenticatedPlayers = new();
        private const float PROXIMITY_RANGE = 15.0f; // Add this line
        private volatile bool isRunning = false;
        
        public UdpVoiceHandler(GameServer server, VoiceHandler voiceHandler)
        {
            gameServer = server;
            voiceUtils = voiceHandler;
        }
        
        public async Task StartUdpVoiceServer(int port = 2051)
        {
            try
            {
                udpServer = new UdpClient(port);
                isRunning = true;
                
                Console.WriteLine($"UDP Voice Server started on port {port} with full feature set");
                Console.WriteLine("Features: Proximity Chat, Priority System, Ignore System, Distance-based Volume, Authentication");
                
                _ = Task.Run(ProcessUdpVoicePackets);
                _ = Task.Run(CleanupInactiveUdpConnections);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to start UDP voice server: {ex.Message}");
                throw;
            }
        }
        
        private async Task ProcessUdpVoicePackets()
        {
            while (isRunning)
            {
                try
                {
                    var result = await udpServer.ReceiveAsync();
                    var packet = result.Buffer;
                    var clientEndpoint = result.RemoteEndPoint;
                    
                    // Check packet type by examining first 4 bytes
                    if (packet.Length >= 4)
                    {
                        string packetType = Encoding.UTF8.GetString(packet, 0, 4);
                        
                        if (packetType == "AUTH")
                        {
                            await ProcessAuthenticationPacket(packet, clientEndpoint);
                            continue;
                        }
                        else if (packetType == "PRIO")
                        {
                            await ProcessPriorityPacket(packet, clientEndpoint);
                            continue;
                        }
                        else if (packetType == "PING")
                        {
                            await ProcessPingPacket(packet, clientEndpoint);
                            continue;
                        }
                    }
                    
                    // Voice data packet - standard format: [16 bytes playerId][Opus audio]
                    if (packet.Length >= 20)
                    {
                        await ProcessVoiceDataPacket(packet, clientEndpoint);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"UDP Voice packet error: {ex.Message}");
                    await Task.Delay(100);
                }
            }
        }
        
        private async Task ProcessAuthenticationPacket(byte[] packet, IPEndPoint clientEndpoint)
        {
            try
            {
                // Auth packet format: "AUTH" + JSON data
                string jsonData = Encoding.UTF8.GetString(packet, 4, packet.Length - 4);
                var authRequest = JsonSerializer.Deserialize<UdpAuthRequest>(jsonData);
                
                Console.WriteLine($"UDP: Authentication request from {authRequest.PlayerId}");
                
                // Validate VoiceID
                if (!ValidateVoiceID(authRequest.PlayerId, authRequest.VoiceId))
                {
                    Console.WriteLine($"UDP SECURITY: Invalid VoiceID for player {authRequest.PlayerId}");
                    await SendAuthResponse(clientEndpoint, "REJECTED", "Invalid VoiceID");
                    return;
                }

                // Verify player session
                if (!VerifyPlayerSession(authRequest.PlayerId))
                {
                    Console.WriteLine($"UDP SECURITY: Player {authRequest.PlayerId} not in active session");
                    await SendAuthResponse(clientEndpoint, "REJECTED", "Not in game");
                    return;
                }

                // Check for duplicate connections
                if (authenticatedPlayers.ContainsKey(authRequest.PlayerId))
                {
                    Console.WriteLine($"UDP: Replacing existing connection for player {authRequest.PlayerId}");
                }

                // Accept authentication
                authenticatedPlayers[authRequest.PlayerId] = true;
                playerUdpEndpoints[authRequest.PlayerId] = clientEndpoint;
                lastUdpActivity[authRequest.PlayerId] = DateTime.UtcNow;
                
                await SendAuthResponse(clientEndpoint, "ACCEPTED", "Voice authenticated");
                Console.WriteLine($"UDP: Voice connection established for player {authRequest.PlayerId}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"UDP authentication error: {ex.Message}");
                await SendAuthResponse(clientEndpoint, "ERROR", "Server error");
            }
        }
        
        private async Task ProcessPriorityPacket(byte[] packet, IPEndPoint clientEndpoint)
        {
            try
            {
                // Priority packet format: "PRIO" + JSON data
                string jsonData = Encoding.UTF8.GetString(packet, 4, packet.Length - 4);
                var priorityCommand = JsonSerializer.Deserialize<UdpPriorityCommand>(jsonData);
                
                // Find player ID from endpoint
                string clientPlayerId = GetPlayerIdFromEndpoint(clientEndpoint);
                if (clientPlayerId == null)
                {
                    Console.WriteLine("UDP: Cannot process priority setting - client not identified");
                    return;
                }
                
                // Get player's world position
                var playerPosition = voiceUtils.GetPlayerPosition(clientPlayerId);
                if (playerPosition != null)
                {
                    var settings = voiceUtils.GetPrioritySettings(playerPosition.WorldId);
                    
                    switch (priorityCommand.SettingType)
                    {
                        case "ENABLED":
                            if (bool.TryParse(priorityCommand.Value, out bool enabled))
                            {
                                settings.EnablePriority = enabled;
                                Console.WriteLine($"UDP: Priority system {(enabled ? "enabled" : "disabled")} for world {playerPosition.WorldId}");
                            }
                            break;
                            
                        case "THRESHOLD":
                            if (int.TryParse(priorityCommand.Value, out int threshold))
                            {
                                settings.ActivationThreshold = threshold;
                                Console.WriteLine($"UDP: Priority threshold set to {threshold} for world {playerPosition.WorldId}");
                            }
                            break;
                            
                        case "NON_PRIORITY_VOLUME":
                            if (float.TryParse(priorityCommand.Value, out float volume))
                            {
                                settings.NonPriorityVolume = volume;
                                Console.WriteLine($"UDP: Non-priority volume set to {volume} for world {playerPosition.WorldId}");
                            }
                            break;
                            
                        case "ADD_MANUAL":
                            if (int.TryParse(priorityCommand.Value, out int addAccountId))
                            {
                                if (settings.AddManualPriority(addAccountId))
                                {
                                    Console.WriteLine($"UDP: Added manual priority for account {addAccountId} in world {playerPosition.WorldId}");
                                }
                                else
                                {
                                    Console.WriteLine($"UDP: Failed to add manual priority - list full ({settings.GetManualPriorityCount()}/{settings.MaxPriorityPlayers})");
                                }
                            }
                            break;
                            
                        case "REMOVE_MANUAL":
                            if (int.TryParse(priorityCommand.Value, out int removeAccountId))
                            {
                                if (settings.RemoveManualPriority(removeAccountId))
                                {
                                    Console.WriteLine($"UDP: Removed manual priority for account {removeAccountId} in world {playerPosition.WorldId}");
                                }
                                else
                                {
                                    Console.WriteLine($"UDP: Account {removeAccountId} was not in manual priority list");
                                }
                            }
                            break;
                    }
                    
                    settings.ValidateSettings();
                    await SendPriorityResponse(clientEndpoint, "SUCCESS", $"Priority setting {priorityCommand.SettingType} updated");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"UDP priority command error: {ex.Message}");
                await SendPriorityResponse(clientEndpoint, "ERROR", "Server error");
            }
        }
        
        private async Task ProcessPingPacket(byte[] packet, IPEndPoint clientEndpoint)
        {
            try
            {
                // Respond to ping with pong
                byte[] pongResponse = Encoding.UTF8.GetBytes("PONG");
                await udpServer.SendAsync(pongResponse, pongResponse.Length, clientEndpoint);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"UDP ping error: {ex.Message}");
            }
        }
        
        private async Task ProcessVoiceDataPacket(byte[] packet, IPEndPoint clientEndpoint)
        {
            try
            {
                // Voice packet format: [16 bytes playerId][Opus audio data]
                string playerId = Encoding.UTF8.GetString(packet, 0, 16).Trim('\0');
                
                // Security check: Player must be authenticated
                if (!authenticatedPlayers.ContainsKey(playerId))
                {
                    Console.WriteLine($"UDP: Rejecting voice from unauthenticated player {playerId}");
                    return;
                }
                
                // Extract Opus audio data
                byte[] opusAudio = new byte[packet.Length - 16];
                Array.Copy(packet, 16, opusAudio, 0, opusAudio.Length);
                
                // Update activity tracking
                playerUdpEndpoints[playerId] = clientEndpoint;
                lastUdpActivity[playerId] = DateTime.UtcNow;
                
                // Create voice data object
                var voiceData = new UdpVoiceData
                {
                    PlayerId = playerId,
                    OpusAudioData = opusAudio,
                    Volume = 1.0f,
                    Timestamp = DateTime.UtcNow
                };
                
                // Broadcast to nearby players with all features
                await BroadcastVoiceToNearbyPlayers(voiceData);
                
                Console.WriteLine($"UDP: Processed {opusAudio.Length} Opus bytes from {playerId}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"UDP voice data processing error: {ex.Message}");
            }
        }
        
        private async Task BroadcastVoiceToNearbyPlayers(UdpVoiceData voiceData)
        {
            try
            {
                // Get speaker position using proximity system
                var speakerPosition = voiceUtils.GetPlayerPosition(voiceData.PlayerId);
                if (speakerPosition == null) 
                {
                    Console.WriteLine($"UDP: Could not find position for player {voiceData.PlayerId}");
                    return;
                }
                
                // Find players in proximity range
                var nearbyPlayers = voiceUtils.GetPlayersInRange(speakerPosition.X, speakerPosition.Y, PROXIMITY_RANGE);
                
                // Get priority settings for this world
                var prioritySettings = voiceUtils.GetPrioritySettings(speakerPosition.WorldId);
                bool prioritySystemActive = voiceUtils.ShouldActivatePrioritySystem(speakerPosition.WorldId, nearbyPlayers.Length);

                Console.WriteLine($"UDP: Broadcasting to {nearbyPlayers.Length} nearby players (Priority: {prioritySystemActive})");
                
                var sendTasks = new List<Task>();
                
                foreach (var player in nearbyPlayers)
                {
                    try
                    {
                        // Skip self-voice
                        if (voiceData.PlayerId == player.PlayerId)
                        {
                            Console.WriteLine($"UDP: Skipping self-voice for player {voiceData.PlayerId}");
                            continue;
                        }

                        // Check ignore system
                        if (voiceUtils.ArePlayersVoiceIgnored(voiceData.PlayerId, player.PlayerId))
                        {
                            Console.WriteLine($"UDP: Voice blocked {voiceData.PlayerId} -> {player.PlayerId} (ignored)");
                            continue;
                        }

                        // Calculate distance-based volume
                        float distanceVolume = Math.Max(0.1f, 1.0f - (player.Distance / PROXIMITY_RANGE));
                        float finalVolume = Math.Max(0.2f, voiceData.Volume * distanceVolume);

                        // Apply priority system
                        if (prioritySystemActive)
                        {
                            bool hasPriority = voiceUtils.HasVoicePriority(voiceData.PlayerId, player.PlayerId, prioritySettings);
                            float volumeMultiplier = prioritySettings.GetVolumeMultiplier(hasPriority);
                            finalVolume *= volumeMultiplier;

                            Console.WriteLine($"UDP: {voiceData.PlayerId} -> {player.PlayerId}: Distance={player.Distance:F1}, Priority={hasPriority}, Volume={finalVolume:F2}");
                        }
                        else
                        {
                            Console.WriteLine($"UDP: {voiceData.PlayerId} -> {player.PlayerId}: Distance={player.Distance:F1}, Volume={finalVolume:F2}");
                        }

                        // Send to player if they have UDP connection
                        if (playerUdpEndpoints.TryGetValue(player.PlayerId, out var targetEndpoint))
                        {
                            // Create response packet with volume info: [16 bytes speakerId][4 bytes volume][Opus audio]
                            byte[] response = new byte[16 + 4 + voiceData.OpusAudioData.Length];
                            
                            // Speaker ID (16 bytes)
                            byte[] speakerIdBytes = Encoding.UTF8.GetBytes(voiceData.PlayerId.PadRight(16));
                            Array.Copy(speakerIdBytes, 0, response, 0, 16);
                            
                            // Volume (4 bytes)
                            byte[] volumeBytes = BitConverter.GetBytes(finalVolume);
                            Array.Copy(volumeBytes, 0, response, 16, 4);
                            
                            // Opus audio data
                            Array.Copy(voiceData.OpusAudioData, 0, response, 20, voiceData.OpusAudioData.Length);
                            
                            sendTasks.Add(SendUdpPacketSafe(response, targetEndpoint, player.PlayerId));
                        }
                        else
                        {
                            Console.WriteLine($"UDP: No endpoint for {player.PlayerId} - player not connected via UDP");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"UDP: Error processing voice for {player.PlayerId}: {ex.Message}");
                    }
                }
                
                if (sendTasks.Count > 0)
                {
                    await Task.WhenAll(sendTasks);
                    Console.WriteLine($"UDP: Successfully broadcasted voice from {voiceData.PlayerId} to {sendTasks.Count} players");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"UDP broadcast error: {ex.Message}");
            }
        }
        
        private async Task SendUdpPacketSafe(byte[] data, IPEndPoint endpoint, string playerId)
        {
            try
            {
                await udpServer.SendAsync(data, data.Length, endpoint);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"UDP: Failed to send to {playerId} at {endpoint}: {ex.Message}");
                // Clean up broken connection
                playerUdpEndpoints.TryRemove(playerId, out _);
                authenticatedPlayers.TryRemove(playerId, out _);
                lastUdpActivity.TryRemove(playerId, out _);
            }
        }
        
        private async Task SendAuthResponse(IPEndPoint endpoint, string status, string message)
        {
            try
            {
                var response = new { Status = status, Message = message };
                string jsonResponse = JsonSerializer.Serialize(response);
                byte[] responseData = Encoding.UTF8.GetBytes("ARSP" + jsonResponse);
                await udpServer.SendAsync(responseData, responseData.Length, endpoint);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"UDP: Error sending auth response: {ex.Message}");
            }
        }
        
        private async Task SendPriorityResponse(IPEndPoint endpoint, string status, string message)
        {
            try
            {
                var response = new { Status = status, Message = message };
                string jsonResponse = JsonSerializer.Serialize(response);
                byte[] responseData = Encoding.UTF8.GetBytes("PRSP" + jsonResponse);
                await udpServer.SendAsync(responseData, responseData.Length, endpoint);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"UDP: Error sending priority response: {ex.Message}");
            }
        }
        
        private string GetPlayerIdFromEndpoint(IPEndPoint endpoint)
        {
            foreach (var kvp in playerUdpEndpoints)
            {
                if (kvp.Value.Equals(endpoint))
                    return kvp.Key;
            }
            return null;
        }
        
        private bool ValidateVoiceID(string playerId, string voiceId)
        {
            try
            {
                var account = gameServer.Database.GetAccount(int.Parse(playerId));
                return account != null && account.VoiceID == voiceId;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"UDP: Error validating VoiceID: {ex.Message}");
                return false;
            }
        }
        
        private bool VerifyPlayerSession(string playerId)
        {
            try
            {
                var clients = gameServer.ConnectionManager.Clients;
                foreach (var clientPair in clients)
                {
                    var client = clientPair.Key;
                    if (client.Player?.AccountId.ToString() == playerId && 
                        client.Socket?.Connected == true && 
                        client.Player.World != null)
                    {
                        return true;
                    }
                }
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"UDP: Error verifying player session: {ex.Message}");
                return false;
            }
        }
        
        private async Task CleanupInactiveUdpConnections()
        {
            while (isRunning)
            {
                try
                {
                    var cutoff = DateTime.UtcNow.AddSeconds(-45); // 45 second timeout
                    var inactivePlayersToRemove = lastUdpActivity
                        .Where(kvp => kvp.Value < cutoff)
                        .Select(kvp => kvp.Key)
                        .ToList();
                    
                    foreach (var playerId in inactivePlayersToRemove)
                    {
                        playerUdpEndpoints.TryRemove(playerId, out _);
                        authenticatedPlayers.TryRemove(playerId, out _);
                        lastUdpActivity.TryRemove(playerId, out _);
                        Console.WriteLine($"UDP: Cleaned up inactive connection for {playerId}");
                    }
                    
                    if (inactivePlayersToRemove.Count > 0)
                    {
                        Console.WriteLine($"UDP: Cleaned up {inactivePlayersToRemove.Count} inactive connections");
                    }
                    
                    await Task.Delay(15000); // Check every 15 seconds
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"UDP cleanup error: {ex.Message}");
                }
            }
        }
        
        public void Stop()
        {
            isRunning = false;
            udpServer?.Close();
            udpServer?.Dispose();
            Console.WriteLine("UDP Voice Server stopped");
        }
        
        // Public status methods
        public int GetConnectedPlayerCount()
        {
            return authenticatedPlayers.Count;
        }
        
        public string[] GetConnectedPlayerIds()
        {
            return authenticatedPlayers.Keys.ToArray();
        }
    }
    
    // Helper classes
    public class PlayerPosition
    {
        public float X { get; set; }
        public float Y { get; set; }
        public int WorldId { get; set; }
    }
    
    public class VoicePlayerInfo 
    {
        public string PlayerId { get; set; }
        public Client Client { get; set; }
        public float Distance { get; set; }
    }
}