//777592
using Shared;

namespace WorldServer.networking.packets.outgoing
{
    public class ProximityVoicePacket : OutgoingMessage
    {
        public string VoiceData { get; set; }

        public ProximityVoicePacket(string voiceData = "")
        {
            VoiceData = voiceData;
        }

        public override MessageId MessageId => MessageId.PROXIMITY_VOICE;

        public override void Write(NetworkWriter wtr)
        {
            wtr.WriteUTF16(VoiceData);
        }
    }
}