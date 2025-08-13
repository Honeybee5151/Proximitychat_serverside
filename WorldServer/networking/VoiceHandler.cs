//777592
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
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

    public class VoiceHandler
    {
        private readonly GameServer gameServer;
        private readonly ConcurrentDictionary<string, TcpClient> voiceConnections = new();
        private const float PROXIMITY_RANGE = 15.0f; // Tiles - adjust as needed
        
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
        client.Close();
    }
}
        
        private async Task<string> ProcessVoiceMessage(string message, TcpClient sender)
        {
            Console.WriteLine($"DEBUG_VOICE: ProcessVoiceMessage called with: {message.Substring(0, Math.Min(20, message.Length))}");
            try
            {
                if (message.StartsWith("VOICE_DATA:"))
                {
                    var jsonData = message.Substring("VOICE_DATA:".Length);
                    var voiceData = JsonSerializer.Deserialize<ChatMessage>(jsonData);
                    
                    // Store this client's connection for future broadcasts
                    voiceConnections[voiceData.PlayerId] = sender;
                    
                    // Get speaker's current position from game server
                    var speakerPosition = GetPlayerPosition(voiceData.PlayerId);
                    if (speakerPosition == null)
                    {
                        Console.WriteLine($"Could not find position for player {voiceData.PlayerId}");
                        return voiceData.PlayerId;
                    }
                    
                    // Find all players within proximity range
                    var nearbyPlayers = GetPlayersInRange(speakerPosition.X, speakerPosition.Y, PROXIMITY_RANGE);
                    
                    // Send voice data to nearby players via their game connections
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
                        voiceConnections[playerId].Close(); // Close old connection
                    }
    
                    voiceConnections[playerId] = sender;
                    Console.WriteLine($"Voice connection established for player {playerId}");
                    return playerId;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing voice message: {ex.Message}");
            }
            
            return null;
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
        private VoicePlayerInfo [] GetPlayersInRange(float speakerX, float speakerY, float range)
        {
            try
            {
                var nearbyPlayers = new List<VoicePlayerInfo >();
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
        foreach (var nearbyPlayer in nearbyPlayers)
        {
            try
            {
                // Skip sending voice back to the speaker
                if (nearbyPlayer.PlayerId == voiceData.PlayerId)
                    continue;

                // Check if players have each other ignored
                if (ArePlayersVoiceIgnored(voiceData.PlayerId, nearbyPlayer.PlayerId))
                {
                    Console.WriteLine($"Voice blocked: {voiceData.PlayerId} <-> {nearbyPlayer.PlayerId} (ignored)");
                    continue; // Skip this player
                }
                    
                // Calculate volume based on distance (closer = louder)
                float distanceVolume = Math.Max(0.1f, 1.0f - (nearbyPlayer.Distance / PROXIMITY_RANGE));
                
                // Create packet with recipient-specific data
                var recipientPacket = new
                {
                    PacketType = "PROXIMITY_VOICE",
                    PlayerId = voiceData.PlayerId,
                    PlayerName = voiceData.PlayerName,
                    AudioData = Convert.ToBase64String(voiceData.AudioData),
                    Volume = voiceData.Volume * distanceVolume,
                    Distance = nearbyPlayer.Distance
                };
                
                // Send via the game connection
                var packetJson = JsonSerializer.Serialize(recipientPacket);
                await SendGamePacketToClient(nearbyPlayer.Client, packetJson);
                
                Console.WriteLine($"Sent voice from {voiceData.PlayerId} to {nearbyPlayer.PlayerId} (distance: {nearbyPlayer.Distance:F1})");
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
        
        private async Task SendGamePacketToClient(Client gameClient, string packetData)
        {
            try
            {
                // Create a proximity voice packet using your existing packet system
                // You'll need to create this packet type that ActionScript can handle
        
                // Option 1: Send as a custom packet
                var voicePacket = new ProximityVoicePacket(packetData);
                gameClient.SendPacket(voicePacket);
        
                // Option 2: Send as a text message (temporary solution)
                // gameClient.SendMessage("PROXIMITY_VOICE", packetData);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending voice packet to client: {ex.Message}");
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