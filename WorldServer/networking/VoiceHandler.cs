//777592
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Shared.database.account;
using Shared.resources;
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
        public float X { get; set; } // Player X position
        public float Y { get; set; } // Player Y position
    }

    public class VoiceGroup
    {
        public string GroupId { get; set; } = Guid.NewGuid().ToString();
        public List<string> PlayerIds { get; set; } = new List<string>();
        public int WorldId { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public int MaxSize { get; set; } = 4;
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

        private readonly ConcurrentDictionary<int, List<VoiceGroup>> dungeonVoiceGroups = new();
        private readonly ConcurrentDictionary<string, string> playerToGroupMapping = new();

        private readonly ConcurrentDictionary<string, bool> playerVoiceEnabled = new();
        
        private readonly ConcurrentQueue<GroupOperation> groupOperationQueue = new();
        private readonly object groupLock = new object();
        private readonly Timer cleanupTimer;
        private readonly Timer heartbeatTimer;

        public VoiceHandler(GameServer server)
        {
            gameServer = server;
            cleanupTimer = new Timer(PerformPeriodicCleanup, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        
            // Heartbeat timer runs every 10 seconds
            heartbeatTimer = new Timer(CheckConnectionHeartbeats, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        
            // Start processing group operations
            Task.Run(ProcessGroupOperations);
        }
        public enum GroupOperationType
        {
            JoinGroup,
            LeaveGroup,
            CleanupWorld
        }

        public class GroupOperation
        {
            public GroupOperationType Type { get; set; }
            public string PlayerId { get; set; }
            public int WorldId { get; set; }
            public TaskCompletionSource<bool> CompletionSource { get; set; } = new();
        }
        private async Task ProcessGroupOperations()
        {
            while (true)
            {
                try
                {
                    if (groupOperationQueue.TryDequeue(out var operation))
                    {
                        lock (groupLock)
                        {
                            switch (operation.Type)
                            {
                                case GroupOperationType.JoinGroup:
                                    AssignPlayerToVoiceGroupInternal(operation.PlayerId, operation.WorldId);
                                    break;
                                case GroupOperationType.LeaveGroup:
                                    RemovePlayerFromGroupInternal(operation.PlayerId);
                                    break;
                                case GroupOperationType.CleanupWorld:
                                    CleanupVoiceGroupsForWorldInternal(operation.WorldId);
                                    break;
                            }
                        }
                        operation.CompletionSource.SetResult(true);
                    }
                    else
                    {
                        await Task.Delay(10); // Small delay when queue is empty
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error processing group operation: {ex.Message}");
                }
            }
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

        public async Task<bool> RemovePlayerFromGroup(string playerId)
        {
            var operation = new GroupOperation
            {
                Type = GroupOperationType.LeaveGroup,
                PlayerId = playerId
            };
        
            groupOperationQueue.Enqueue(operation);
            return await operation.CompletionSource.Task;
        }
  private void AssignPlayerToVoiceGroupInternal(string playerId, int worldId)
    {
        if (!playerVoiceEnabled.GetValueOrDefault(playerId, true))
        {
            Console.WriteLine($"Player {playerId} has voice disabled - not adding to group");
            return;
        }

        // Remove from any existing group first
        RemovePlayerFromGroupInternal(playerId);

        if (!dungeonVoiceGroups.ContainsKey(worldId))
        {
            dungeonVoiceGroups[worldId] = new List<VoiceGroup>();
        }

        var groups = dungeonVoiceGroups[worldId];
        var availableGroup = groups.FirstOrDefault(g => g.PlayerIds.Count < g.MaxSize);

        if (availableGroup == null)
        {
            availableGroup = new VoiceGroup
            {
                WorldId = worldId,
                MaxSize = 4
            };
            groups.Add(availableGroup);
        }

        availableGroup.PlayerIds.Add(playerId);
        playerToGroupMapping[playerId] = availableGroup.GroupId;

        Console.WriteLine($"Player {playerId} assigned to group {availableGroup.GroupId} in world {worldId} (size: {availableGroup.PlayerIds.Count})");
    }

    private void RemovePlayerFromGroupInternal(string playerId)
    {
        if (!playerToGroupMapping.ContainsKey(playerId)) return;

        var groupId = playerToGroupMapping[playerId];
        playerToGroupMapping.TryRemove(playerId, out _);

        // Find and update the group
        foreach (var worldGroups in dungeonVoiceGroups.Values)
        {
            var group = worldGroups.FirstOrDefault(g => g.GroupId == groupId);
            if (group != null)
            {
                group.PlayerIds.Remove(playerId);
                if (group.PlayerIds.Count == 0)
                {
                    worldGroups.Remove(group);
                    Console.WriteLine($"Removed empty group {group.GroupId} from world {group.WorldId}");
                }
                break;
            }
        }

        // Clean up empty world entries
        var emptyWorlds = dungeonVoiceGroups.Where(kv => kv.Value.Count == 0).ToList();
        foreach (var emptyWorld in emptyWorlds)
        {
            dungeonVoiceGroups.TryRemove(emptyWorld.Key, out _);
            Console.WriteLine($"Removed empty world {emptyWorld.Key} from voice groups");
        }
    }

    // NEW: Periodic cleanup to handle orphaned data
    private void PerformPeriodicCleanup(object state)
    {
        try
        {
            Console.WriteLine("Starting periodic voice system cleanup...");
            
            // Clean up dead connections
            var deadConnections = new List<string>();
            foreach (var connection in voiceConnections)
            {
                try
                {
                    if (!connection.Value.Connected || !VerifyPlayerSession(connection.Key))
                    {
                        deadConnections.Add(connection.Key);
                    }
                }
                catch
                {
                    deadConnections.Add(connection.Key);
                }
            }

            foreach (var deadPlayerId in deadConnections)
            {
                Console.WriteLine($"Cleaning up dead connection for player {deadPlayerId}");
                voiceConnections.TryRemove(deadPlayerId, out var deadClient);
                try
                {
                    deadClient?.Close();
                }
                catch { }
                
                // Remove from group as well
                _ = Task.Run(() => RemovePlayerFromGroup(deadPlayerId));
            }

            // Clean up groups with no active players
            lock (groupLock)
            {
                var worldsToClean = new List<int>();
                foreach (var worldEntry in dungeonVoiceGroups)
                {
                    var groupsToRemove = new List<VoiceGroup>();
                    
                    foreach (var group in worldEntry.Value)
                    {
                        var activePlayersInGroup = group.PlayerIds.Where(VerifyPlayerSession).ToList();
                        
                        if (activePlayersInGroup.Count != group.PlayerIds.Count)
                        {
                            // Remove inactive players from group
                            var inactivePlayers = group.PlayerIds.Except(activePlayersInGroup).ToList();
                            foreach (var inactivePlayer in inactivePlayers)
                            {
                                group.PlayerIds.Remove(inactivePlayer);
                                playerToGroupMapping.TryRemove(inactivePlayer, out _);
                                Console.WriteLine($"Removed inactive player {inactivePlayer} from group {group.GroupId}");
                            }
                        }

                        if (group.PlayerIds.Count == 0)
                        {
                            groupsToRemove.Add(group);
                        }
                    }

                    foreach (var emptyGroup in groupsToRemove)
                    {
                        worldEntry.Value.Remove(emptyGroup);
                        Console.WriteLine($"Removed empty group {emptyGroup.GroupId} from world {worldEntry.Key}");
                    }

                    if (worldEntry.Value.Count == 0)
                    {
                        worldsToClean.Add(worldEntry.Key);
                    }
                }

                foreach (var worldId in worldsToClean)
                {
                    dungeonVoiceGroups.TryRemove(worldId, out _);
                    Console.WriteLine($"Removed empty world {worldId} from voice groups");
                }
            }

            Console.WriteLine($"Cleanup complete. Active connections: {voiceConnections.Count}, Active groups: {dungeonVoiceGroups.Sum(w => w.Value.Count)}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error during periodic cleanup: {ex.Message}");
        }
    }
    private void CheckConnectionHeartbeats(object state)
    {
        var connectionsToCheck = voiceConnections.ToList();
        
        foreach (var connection in connectionsToCheck)
        {
            try
            {
                if (!connection.Value.Connected)
                {
                    Console.WriteLine($"Connection heartbeat failed for player {connection.Key}");
                    voiceConnections.TryRemove(connection.Key, out _);
                    _ = Task.Run(() => RemovePlayerFromGroup(connection.Key));
                }
                else
                {
                    // Send a small heartbeat message
                    var heartbeat = Encoding.UTF8.GetBytes("HEARTBEAT");
                    var lengthHeader = BitConverter.GetBytes(heartbeat.Length);
                    
                    var stream = connection.Value.GetStream();
                    stream.WriteAsync(lengthHeader, 0, 4);
                    stream.WriteAsync(heartbeat, 0, heartbeat.Length);
                    stream.FlushAsync();
                }
            }
            catch
            {
                // Connection is dead
                voiceConnections.TryRemove(connection.Key, out _);
                _ = Task.Run(() => RemovePlayerFromGroup(connection.Key));
            }
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

                    Console.WriteLine(
                        $"DEBUG: Received length header: {lengthBuffer[0]},{lengthBuffer[1]},{lengthBuffer[2]},{lengthBuffer[3]} = {messageLength} bytes");

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
                        int bytesRead = await stream.ReadAsync(messageBuffer, messageBytesRead,
                            messageLength - messageBytesRead);
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
                if (clientPlayerId != null)
                {
                    // Remove from voice group before removing connection
                    RemovePlayerFromGroup(clientPlayerId);

                    if (voiceConnections.ContainsKey(clientPlayerId))
                    {
                        voiceConnections.TryRemove(clientPlayerId, out _);
                    }
                }

                try
                {
                    client.Close();
                }
                catch
                {
                }
            }
        }


      private async Task<string> ProcessVoiceMessage(string message, TcpClient sender)
    {
        string clientPlayerId = null;

        try
        {
            if (message.StartsWith("VOICE_DATA:"))
            {
                var jsonData = message.Substring("VOICE_DATA:".Length);
                var voiceData = JsonSerializer.Deserialize<ChatMessage>(jsonData);

                voiceConnections[voiceData.PlayerId] = sender;
                clientPlayerId = voiceData.PlayerId;

                var speakerPosition = GetPlayerPosition(voiceData.PlayerId);
                if (speakerPosition == null)
                {
                    Console.WriteLine($"Could not find position for player {voiceData.PlayerId}");
                    return voiceData.PlayerId;
                }

                await BroadcastVoiceToGroup(voiceData, speakerPosition);
                return voiceData.PlayerId;
            }
            else if (message.StartsWith("VOICE_CONNECT:"))
            {
                var parts = message.Substring("VOICE_CONNECT:".Length).Split(':');
                if (parts.Length < 2)
                {
                    Console.WriteLine("Invalid voice connect format");
                    sender.Close();
                    return null;
                }

                var playerId = parts[0];
                var voiceId = parts[1];

                if (!ValidateVoiceID(playerId, voiceId) || !VerifyPlayerSession(playerId))
                {
                    Console.WriteLine($"SECURITY: Invalid connection attempt for player {playerId}");
                    sender.Close();
                    return null;
                }

                // Close existing connection if any
                if (voiceConnections.ContainsKey(playerId))
                {
                    try
                    {
                        voiceConnections[playerId].Close();
                    }
                    catch { }
                }

                voiceConnections[playerId] = sender;
                clientPlayerId = playerId;
                playerVoiceEnabled[clientPlayerId] = true;

                // Handle world transitions and group assignment
                var playerPos = GetPlayerPosition(playerId);
                if (playerPos != null)
                {
                    // Remove from old group if in different world
                    if (playerToGroupMapping.ContainsKey(playerId))
                    {
                        var currentGroup = FindGroupByPlayerId(playerId);
                        if (currentGroup != null && currentGroup.WorldId != playerPos.WorldId)
                        {
                            await RemovePlayerFromGroup(playerId);
                        }
                    }

                    // Assign to new group if in dungeon
                    if (IsDungeon(playerPos.WorldId))
                    {
                        await AssignPlayerToVoiceGroup(playerId, playerPos.WorldId);
                    }
                }

                Console.WriteLine($"Voice connection established for player {playerId}");
                return playerId;
            }
            else if (message.StartsWith("VOICE_DISCONNECT:"))
            {
                var parts = message.Substring("VOICE_DISCONNECT:".Length).Split(':');
                if (parts.Length >= 1)
                {
                    var playerId = parts[0];
                    playerVoiceEnabled[playerId] = false;
                    voiceConnections.TryRemove(playerId, out _);
                    await RemovePlayerFromGroup(playerId);
                    
                    Console.WriteLine($"Player {playerId} disconnected from voice system");
                    return playerId;
                }
            }
            else if (message == "HEARTBEAT")
            {
                // Heartbeat received - connection is alive
                return clientPlayerId;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error processing voice message: {ex.Message}");
        }

        return clientPlayerId;
    }
      

// Helper methods you'll need to add:
private VoiceGroup FindGroupByPlayerId(string playerId)
{
    if (!playerToGroupMapping.ContainsKey(playerId)) return null;
    
    var groupId = playerToGroupMapping[playerId];
    foreach (var worldGroups in dungeonVoiceGroups.Values)
    {
        var group = worldGroups.FirstOrDefault(g => g.GroupId == groupId);
        if (group != null) return group;
    }
    return null;
}



public async Task<bool> AssignPlayerToVoiceGroup(string playerId, int worldId)
{
    var operation = new GroupOperation
    {
        Type = GroupOperationType.JoinGroup,
        PlayerId = playerId,
        WorldId = worldId
    };
        
    groupOperationQueue.Enqueue(operation);
    return await operation.CompletionSource.Task;
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
        
        
        
        private async Task BroadcastVoiceToGroup(ChatMessage voiceData, PlayerPosition speakerPosition)
        {
            if (!playerToGroupMapping.ContainsKey(voiceData.PlayerId))
            {
                Console.WriteLine($"Player {voiceData.PlayerId} not in any voice group");
                return;
            }

            var groupId = playerToGroupMapping[voiceData.PlayerId];
            var group = FindGroupById(groupId, speakerPosition.WorldId);

            if (group == null) return;

            foreach (var memberId in group.PlayerIds)
            {
                if (memberId == voiceData.PlayerId) continue; // Skip self
                if (!playerVoiceEnabled.GetValueOrDefault(memberId, true)) continue;
                if (ArePlayersVoiceIgnored(voiceData.PlayerId, memberId)) continue;

                // NEW: Check distance within the group
                var listenerPosition = GetPlayerPosition(memberId);
                if (listenerPosition == null) continue;

                float distance = CalculateDistance(
                    speakerPosition.X, speakerPosition.Y,
                    listenerPosition.X, listenerPosition.Y
                );

                // Only send audio if within range
                if (distance <= PROXIMITY_RANGE)
                {
                    // Optional: Apply distance-based volume attenuation
                    float adjustedVolume = CalculateVolumeByDistance(voiceData.Volume, distance);
            
                    await SendAudioToClientTCP(memberId, voiceData.AudioData, adjustedVolume, voiceData.PlayerId);
                    Console.WriteLine($"Sent audio to {memberId} at distance {distance:F1} tiles (volume: {adjustedVolume:F2})");
                }
                else
                {
                    Console.WriteLine($"Player {memberId} out of range ({distance:F1} > {PROXIMITY_RANGE} tiles)");
                }
            }
        }
        private float CalculateVolumeByDistance(float originalVolume, float distance)
        {
            if (distance <= 1.0f) return originalVolume; // Full volume up close
    
            // Linear falloff: volume decreases as distance increases
            float attenuation = Math.Max(0.1f, 1.0f - (distance / PROXIMITY_RANGE));
            return originalVolume * attenuation;
        }
        
        private bool IsDungeon(int worldId)
        {
            try
            {
                var world = gameServer.WorldManager.GetWorld(worldId);
                if (world == null) return false;

                // Check if the world instance type is Dungeon
                return world.InstanceType == WorldResourceInstanceType.Dungeon;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error checking if world {worldId} is dungeon: {ex.Message}");
                return false;
            }
        }
        public async Task CleanupVoiceGroupsForWorld(int worldId)
        {
            var operation = new GroupOperation
            {
                Type = GroupOperationType.CleanupWorld,
                WorldId = worldId
            };
        
            groupOperationQueue.Enqueue(operation);
            await operation.CompletionSource.Task;
        }
        private void CleanupVoiceGroupsForWorldInternal(int worldId)
        {
            if (dungeonVoiceGroups.ContainsKey(worldId))
            {
                var groups = dungeonVoiceGroups[worldId];
                foreach (var group in groups)
                {
                    foreach (var playerId in group.PlayerIds)
                    {
                        playerToGroupMapping.TryRemove(playerId, out _);
                    }
                }
                dungeonVoiceGroups.TryRemove(worldId, out _);
                Console.WriteLine($"Cleaned up voice groups for completed world {worldId}");
            }
        }
        private VoiceGroup FindGroupById(string groupId, int worldId)
        {
            if (!dungeonVoiceGroups.ContainsKey(worldId)) return null;
            return dungeonVoiceGroups[worldId].FirstOrDefault(g => g.GroupId == groupId);
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

        public void Dispose()
        {
            cleanupTimer?.Dispose();
            heartbeatTimer?.Dispose();
        
            // Close all connections
            foreach (var connection in voiceConnections.Values)
            {
                try
                {
                    connection.Close();
                }
                catch { }
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