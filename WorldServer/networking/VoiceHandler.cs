using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using WorldServer.core;

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
        
        public async Task HandleVoiceClient(TcpClient client)
        {
            var buffer = new byte[8192];
            var stream = client.GetStream();
            string clientPlayerId = null;
            
            try
            {
                while (client.Connected)
                {
                    int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                    if (bytesRead > 0)
                    {
                        string message = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                        clientPlayerId = await ProcessVoiceMessage(message, client);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Voice client error: {ex.Message}");
            }
            finally
            {
                // Remove client from connections when they disconnect
                if (clientPlayerId != null && voiceConnections.ContainsKey(clientPlayerId))
                {
                    voiceConnections.TryRemove(clientPlayerId, out _);
                }
                client.Close();
            }
        }
        
        private async Task<string> ProcessVoiceMessage(string message, TcpClient sender)
        {
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
                    var playerId = message.Substring("VOICE_CONNECT:".Length);
                    voiceConnections[playerId] = sender;
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
                // Create proximity voice packet for ActionScript clients
                var proximityVoicePacket = new
                {
                    PacketType = "PROXIMITY_VOICE",
                    PlayerId = voiceData.PlayerId,
                    PlayerName = voiceData.PlayerName,
                    AudioData = Convert.ToBase64String(voiceData.AudioData),
                    Volume = voiceData.Volume,
                    Distance = 0f // Will be calculated per recipient
                };
                
                foreach (var nearbyPlayer in nearbyPlayers)
                {
                    try
                    {
                        // Skip sending voice back to the speaker
                        if (nearbyPlayer.PlayerId == voiceData.PlayerId)
                            continue;
                            
                        // Calculate volume based on distance (closer = louder)
                        float distanceVolume = Math.Max(0.1f, 1.0f - (nearbyPlayer.Distance / PROXIMITY_RANGE));
                        
                        // Update packet with recipient-specific data
                        var recipientPacket = new
                        {
                            PacketType = "PROXIMITY_VOICE",
                            PlayerId = voiceData.PlayerId,
                            PlayerName = voiceData.PlayerName,
                            AudioData = Convert.ToBase64String(voiceData.AudioData),
                            Volume = voiceData.Volume * distanceVolume,
                            Distance = nearbyPlayer.Distance
                        };
                        
                        // Send via the game connection (you'll need to adapt this to your packet system)
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
        
        private async Task SendGamePacketToClient(object client, string packetData)
        {
            try
            {
                // You'll need to adapt this to your server's packet sending system
                // This is a placeholder - you'll need to use your existing packet infrastructure
                
                // Example approach:
                // 1. Create a custom packet type for proximity voice
                // 2. Send it through the existing game connection
                // 3. ActionScript client receives it and plays the audio
                
                Console.WriteLine($"Would send packet to client: {packetData}");
                
                // TODO: Implement actual packet sending using your server's packet system
                // This might involve:
                // - Creating a ProximityVoicePacket class
                // - Using your existing packet sending infrastructure
                // - Sending to the client's game connection
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending game packet: {ex.Message}");
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
        public object Client { get; set; } // Your Client type
        public float Distance { get; set; }
    }
}