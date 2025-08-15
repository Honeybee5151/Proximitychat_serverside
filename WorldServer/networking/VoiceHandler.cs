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
    public class ChatMessage
    {
        public string PlayerId { get; set; }
        public string PlayerName { get; set; }
        public byte[] AudioData { get; set; }
        public float Volume { get; set; }
        public DateTime Timestamp { get; set; }
        public float X { get; set; }  // Player X position
        public float Y { get; set; }  // Player Y position
    }

    // New class for outgoing audio messages
    public class AudioMessage
    {
        public string Type { get; set; } = "VOICE_AUDIO";
        public string AudioData { get; set; } // Base64 encoded
        public float Volume { get; set; }
        public DateTime Timestamp { get; set; }
        public string SpeakerId { get; set; }
    }

    public class VoiceHandler
    {
        private readonly GameServer gameServer;
        private readonly ConcurrentDictionary<string, TcpClient> voiceConnections = new();
        private const float PROXIMITY_RANGE = 15.0f; // Tiles - adjust as needed
        private readonly ConcurrentDictionary<int, VoicePrioritySettings> worldPrioritySettings = new();
        
        public VoiceHandler(GameServer server)
        {
            gameServer = server;
        }
        
        private bool ArePlayersVoiceIgnored(string speakerId, string listenerId)
        {
            try
            {
                // Get both player accounts
                var speakerAccount = gameServer.Database.GetAccount(int.Parse(speakerId));
                var listenerAccount = gameServer.Database.GetAccount(int.Parse(listenerId));
        
                if (speakerAccount == null || listenerAccount == null)
                    return false;

                // Check if listener has speaker ignored (listener won't hear speaker)
                bool listenerIgnoresSpeaker = listenerAccount.IgnoreList.Contains(speakerAccount.AccountId);
        
                // Check if speaker has listener ignored (speaker's voice won't go to listener)
                bool speakerIgnoresListener = speakerAccount.IgnoreList.Contains(listenerAccount.AccountId);
        
                // Block voice if either player ignores the other
                return listenerIgnoresSpeaker || speakerIgnoresListener;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error checking voice ignore status: {ex.Message}");
                return false; // Default to allowing voice if error
            }
        }
       
        public async Task HandleVoiceClient(TcpClient client)
        {
            Console.WriteLine($"DEBUG_VOICE: HandleVoiceClient called from {client.Client.RemoteEndPoint}");
            var stream = client.GetStream();
            string clientPlayerId = null;
            
            try
            {
                while (client.Connected)
                {
                    // Read 4-byte length header
                    var lengthBuffer = new byte[4];
                    int lengthBytesRead = 0;
                    
                    while (lengthBytesRead < 4)
                    {
                        int bytesRead = await stream.ReadAsync(lengthBuffer, lengthBytesRead, 4 - lengthBytesRead);
                        if (bytesRead == 0) break;
                        lengthBytesRead += bytesRead;
                    }
                    
                    if (lengthBytesRead < 4) break;
                    
                    // Parse length using consistent byte order (little-endian)
                    int messageLength = lengthBuffer[0] | 
                                      (lengthBuffer[1] << 8) | 
                                      (lengthBuffer[2] << 16) | 
                                      (lengthBuffer[3] << 24);
                    
                    Console.WriteLine($"DEBUG: Received length header: {lengthBuffer[0]},{lengthBuffer[1]},{lengthBuffer[2]},{lengthBuffer[3]} = {messageLength} bytes");
                    
                    if (messageLength > 100000 || messageLength < 0) // 100KB safety limit
                    {
                        Console.WriteLine($"Invalid message length: {messageLength} bytes, disconnecting client");
                        break;
                    }
                    
                    // Read the actual message
                    var messageBuffer = new byte[messageLength];
                    int messageBytesRead = 0;
                    
                    while (messageBytesRead < messageLength)
                    {
                        int bytesRead = await stream.ReadAsync(messageBuffer, messageBytesRead, messageLength - messageBytesRead);
                        if (bytesRead == 0) break;
                        messageBytesRead += bytesRead;
                    }
                    
                    if (messageBytesRead < messageLength) break;
                    
                    string message = Encoding.UTF8.GetString(messageBuffer, 0, messageLength);
                    Console.WriteLine($"DEBUG: Received complete message, {messageLength} bytes");
                    
                    clientPlayerId = await ProcessVoiceMessage(message, client);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Voice client error: {ex.Message}");
            }
            finally
            {
                if (clientPlayerId != null && voiceConnections.ContainsKey(clientPlayerId))
                {
                    voiceConnections.TryRemove(clientPlayerId, out _);
                }
                try
                {
                    client.Close();
                }
                catch { }
            }
        }
        
        private async Task<string> ProcessVoiceMessage(string message, TcpClient sender)
        {
            Console.WriteLine($"DEBUG_VOICE: ProcessVoiceMessage called with: {message.Substring(0, Math.Min(20, message.Length))}");
            
            // DECLARE clientPlayerId at the top of the method
            string clientPlayerId = null;
            
            try
            {
                if (message.StartsWith("VOICE_DATA:"))
                {
                    var jsonData = message.Substring("VOICE_DATA:".Length);
                    var voiceData = JsonSerializer.Deserialize<ChatMessage>(jsonData);
                    
                    // Store this client's connection for future broadcasts
                    voiceConnections[voiceData.PlayerId] = sender;
                    clientPlayerId = voiceData.PlayerId; // SET the variable here
                    
                    // Get speaker's current position from game server
                    var speakerPosition = GetPlayerPosition(voiceData.PlayerId);
                    if (speakerPosition == null)
                    {
                        Console.WriteLine($"Could not find position for player {voiceData.PlayerId}");
                        return voiceData.PlayerId;
                    }
                    
                    // Find all players within proximity range
                    var nearbyPlayers = GetPlayersInRange(speakerPosition.X, speakerPosition.Y, PROXIMITY_RANGE);
                    
                    // Send voice data to nearby players via their TCP connections
                    await BroadcastVoiceToNearbyPlayers(voiceData, nearbyPlayers, speakerPosition);
                    
                    return voiceData.PlayerId;
                }
                else if (message.StartsWith("VOICE_CONNECT:"))
                {
                    // Handle initial connection - player identifying themselves
                    // Format: VOICE_CONNECT:playerID:voiceID
                    var parts = message.Substring("VOICE_CONNECT:".Length).Split(':');
                    if (parts.Length < 2)
                    {
                        Console.WriteLine("Invalid voice connect format - missing voiceID");
                        sender.Close();
                        return null;
                    }

                    var playerId = parts[0];
                    var voiceId = parts[1];

                    // Validate VoiceID belongs to this player
                    if (!ValidateVoiceID(playerId, voiceId))
                    {
                        Console.WriteLine($"SECURITY: Invalid VoiceID for player {playerId}");
                        sender.Close();
                        return null;
                    }

                    // Check if player is actually in game
                    if (!VerifyPlayerSession(playerId))
                    {
                        Console.WriteLine($"SECURITY: Player {playerId} not in active game session");
                        sender.Close();
                        return null;
                    }

                    // Check for duplicate connections
                    if (voiceConnections.ContainsKey(playerId))
                    {
                        Console.WriteLine($"SECURITY: Closing duplicate voice connection for player {playerId}");
                        try
                        {
                            voiceConnections[playerId].Close(); // Close old connection
                        }
                        catch { }
                    }

                    voiceConnections[playerId] = sender;
                    clientPlayerId = playerId; // SET the variable here
                    Console.WriteLine($"Voice connection established for player {playerId}");
                    return playerId;
                }
                else if (message.StartsWith("PRIORITY_SETTING:"))
                {
                    // Format: PRIORITY_SETTING:settingType:value
                    var parts = message.Substring("PRIORITY_SETTING:".Length).Split(':');
                    if (parts.Length >= 2)
                    {
                        var settingType = parts[0];
                        var value = parts[1];
                        
                        // Find clientPlayerId from the voice connection if not already set
                        if (clientPlayerId == null)
                        {
                            foreach (var conn in voiceConnections)
                            {
                                if (conn.Value == sender)
                                {
                                    clientPlayerId = conn.Key;
                                    break;
                                }
                            }
                        }
                        
                        if (clientPlayerId == null)
                        {
                            Console.WriteLine("Cannot process priority setting - client not identified");
                            return null;
                        }
                        
                        // Get the player's world ID to apply settings to the correct world
                        var playerPosition = GetPlayerPosition(clientPlayerId);
                        if (playerPosition != null)
                        {
                            var settings = GetPrioritySettings(playerPosition.WorldId);
                            
                            switch (settingType)
                            {
                                case "ENABLED":
                                    if (bool.TryParse(value, out bool enabled))
                                    {
                                        settings.EnablePriority = enabled;
                                        Console.WriteLine($"Priority system {(enabled ? "enabled" : "disabled")} for world {playerPosition.WorldId}");
                                    }
                                    break;
                                    
                                case "THRESHOLD":
                                    if (int.TryParse(value, out int threshold))
                                    {
                                        settings.ActivationThreshold = threshold;
                                        Console.WriteLine($"Priority threshold set to {threshold} for world {playerPosition.WorldId}");
                                    }
                                    break;
                                    
                                case "NON_PRIORITY_VOLUME":
                                    if (float.TryParse(value, out float volume))
                                    {
                                        settings.NonPriorityVolume = volume;
                                        Console.WriteLine($"Non-priority volume set to {volume} for world {playerPosition.WorldId}");
                                    }
                                    break;
                                    
                                case "ADD_MANUAL":
                                    if (int.TryParse(value, out int addAccountId))
                                    {
                                        if (settings.AddManualPriority(addAccountId))
                                        {
                                            Console.WriteLine($"Added manual priority for account {addAccountId} in world {playerPosition.WorldId}");
                                        }
                                        else
                                        {
                                            Console.WriteLine($"Failed to add manual priority - list full ({settings.GetManualPriorityCount()}/{settings.MaxPriorityPlayers})");
                                        }
                                    }
                                    break;
                                    
                                case "REMOVE_MANUAL":
                                    if (int.TryParse(value, out int removeAccountId))
                                    {
                                        if (settings.RemoveManualPriority(removeAccountId))
                                        {
                                            Console.WriteLine($"Removed manual priority for account {removeAccountId} in world {playerPosition.WorldId}");
                                        }
                                        else
                                        {
                                            Console.WriteLine($"Account {removeAccountId} was not in manual priority list");
                                        }
                                    }
                                    break;
                            }
                            
                            // Validate settings after any changes
                            settings.ValidateSettings();
                        }
                    }
                    
                    return clientPlayerId;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing voice message: {ex.Message}");
            }
            
            return clientPlayerId; // Return whatever clientPlayerId was set to, or null
        }
        
        private PlayerPosition GetPlayerPosition(string playerId)
        {
            try
            {
                // You'll need to adapt this to your server's player management system
                // This is a common pattern - find the client/player by ID and get their position
                
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
        
        private bool ValidateVoiceID(string playerId, string voiceId)
        {
            try
            {
                // Get the account from database and check VoiceID matches
                var account = gameServer.Database.GetAccount(int.Parse(playerId));
                return account != null && account.VoiceID == voiceId;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error validating VoiceID: {ex.Message}");
                return false;
            }
        }
        
        private bool VerifyPlayerSession(string playerId)
        {
            try
            {
                // Check if player is actually logged into the game server
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
                return false; // Player not found or not actively playing
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error verifying player session: {ex.Message}");
                return false;
            }
        }
        
        private VoicePlayerInfo[] GetPlayersInRange(float speakerX, float speakerY, float range)
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
        
        private async Task BroadcastVoiceToNearbyPlayers(ChatMessage voiceData, VoicePlayerInfo[] nearbyPlayers, PlayerPosition speakerPosition)
        {
            try
            {
                // Get priority settings for this world
                var prioritySettings = GetPrioritySettings(speakerPosition.WorldId);
                bool prioritySystemActive = ShouldActivatePrioritySystem(speakerPosition.WorldId, nearbyPlayers.Length);

                Console.WriteLine($"Priority system active: {prioritySystemActive} ({nearbyPlayers.Length} players nearby)");

                foreach (var nearbyPlayer in nearbyPlayers)
                {
                    try
                    {
                        // Uncomment these lines if you want to skip self-voice
                         if (voiceData.PlayerId == nearbyPlayer.PlayerId)
                         {
                             Console.WriteLine($"Skipping self-voice for player {voiceData.PlayerId}");
                             continue;
                         }

                        // Check if players have each other ignored
                        if (ArePlayersVoiceIgnored(voiceData.PlayerId, nearbyPlayer.PlayerId))
                        {
                            Console.WriteLine($"Voice blocked: {voiceData.PlayerId} <-> {nearbyPlayer.PlayerId} (ignored)");
                            continue;
                        }

                        // Calculate base volume based on distance - increased minimum volume for testing
                        float distanceVolume = Math.Max(0.3f, 1.0f - (nearbyPlayer.Distance / PROXIMITY_RANGE));
                        float finalVolume = Math.Max(0.5f, voiceData.Volume * distanceVolume); // Force minimum volume

                        // Apply priority system if active
                        if (prioritySystemActive)
                        {
                            bool hasPriority = HasVoicePriority(voiceData.PlayerId, nearbyPlayer.PlayerId, prioritySettings);
                            float volumeMultiplier = prioritySettings.GetVolumeMultiplier(hasPriority);
                            finalVolume *= volumeMultiplier;

                            Console.WriteLine($"Player {voiceData.PlayerId} -> {nearbyPlayer.PlayerId}: Priority={hasPriority}, Volume={finalVolume:F2}");
                        }

                        // Send audio with calculated volume via TCP
                        await SendAudioToClientTCP(nearbyPlayer.PlayerId, voiceData.AudioData, finalVolume, voiceData.PlayerId);

                        Console.WriteLine($"Sent voice from {voiceData.PlayerId} to {nearbyPlayer.PlayerId} (distance: {nearbyPlayer.Distance:F1}, volume: {finalVolume:F2})");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error sending voice to player {nearbyPlayer.PlayerId}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error broadcasting voice: {ex.Message}");
            }
        }

        // NEW METHOD: Send audio via TCP instead of UDP
        private async Task SendAudioToClientTCP(string targetPlayerId, byte[] audioData, float volume, string speakerId)
        {
            try
            {
                // Check if we have a voice connection for this player
                if (!voiceConnections.ContainsKey(targetPlayerId))
                {
                    Console.WriteLine($"No voice connection found for player {targetPlayerId}");
                    return;
                }

                var voiceClient = voiceConnections[targetPlayerId];
                if (!voiceClient.Connected)
                {
                    Console.WriteLine($"Voice connection not active for player {targetPlayerId}");
                    voiceConnections.TryRemove(targetPlayerId, out _);
                    return;
                }

                // Create audio message
                var audioMessage = new AudioMessage
                {
                    Type = "VOICE_AUDIO",
                    AudioData = Convert.ToBase64String(audioData),
                    Volume = volume,
                    Timestamp = DateTime.UtcNow,
                    SpeakerId = speakerId
                };

                string jsonMessage = JsonSerializer.Serialize(audioMessage);
                byte[] messageBytes = Encoding.UTF8.GetBytes(jsonMessage);
                
                // Send length header (4 bytes, little-endian)
                byte[] lengthHeader = BitConverter.GetBytes(messageBytes.Length);
                
                var stream = voiceClient.GetStream();
                await stream.WriteAsync(lengthHeader, 0, 4);
                await stream.WriteAsync(messageBytes, 0, messageBytes.Length);
                await stream.FlushAsync();

                Console.WriteLine($"Sent {audioData.Length} bytes via TCP to player {targetPlayerId} (volume: {volume:F2})");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending audio via TCP to player {targetPlayerId}: {ex.Message}");
                // Clean up broken connection
                voiceConnections.TryRemove(targetPlayerId, out _);
            }
        }

        // LEGACY METHOD: Keep for reference but not used anymore
        private async Task SendAudioToClient(Client gameClient, byte[] audioData, float volume)
        {
            try
            {
                // Get client's IP from existing connection
                var clientEndpoint = gameClient.Socket?.RemoteEndPoint as IPEndPoint;
                if (clientEndpoint == null)
                {
                    Console.WriteLine($"Could not get IP for client {gameClient.Player?.AccountId}");
                    return;
                }

                // Send raw audio data to client's VoiceManager on port 2051
                var voiceEndpoint = new IPEndPoint(clientEndpoint.Address, 2051);

                using (var udpClient = new UdpClient())
                {
                    await udpClient.SendAsync(audioData, audioData.Length, voiceEndpoint);
                }

                Console.WriteLine($"Sent {audioData.Length} bytes to {clientEndpoint.Address}:2051");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending audio to client: {ex.Message}");
            }
        }
        
        private VoicePrioritySettings GetPrioritySettings(int worldId)
        {
            return worldPrioritySettings.GetOrAdd(worldId, _ => new VoicePrioritySettings());
        }

        private bool ShouldActivatePrioritySystem(int worldId, int nearbyPlayerCount)
        {
            var settings = GetPrioritySettings(worldId);
            if (!settings.EnablePriority) return false;
    
            // Default activation threshold of 8 players (you can make this configurable later)
            int activationThreshold = 8;
            return nearbyPlayerCount >= activationThreshold;
        }

        private bool HasVoicePriority(string playerId, string listenerId, VoicePrioritySettings settings)
        {
            try
            {
                int playerAccountId = int.Parse(playerId);
                int listenerAccountId = int.Parse(listenerId);
        
                // Check manual priority list
                if (settings.HasManualPriority(playerAccountId))
                    return true;

                var playerAccount = gameServer.Database.GetAccount(playerAccountId);
                var listenerAccount = gameServer.Database.GetAccount(listenerAccountId);

                if (playerAccount == null || listenerAccount == null)
                    return false;

                // Check guild priority
                if (settings.GuildMembersGetPriority && playerAccount.GuildId > 0)
                {
                    if (playerAccount.GuildId == listenerAccount.GuildId)
                        return true;
                }

                // Check locked player priority - if listener has speaker locked, give speaker priority
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
        public Client Client { get; set; } // Your Client type
        public float Distance { get; set; }
    }
}