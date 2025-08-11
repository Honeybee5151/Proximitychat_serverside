//777592

using System;

namespace WorldServer.utils;

public static class VoiceUtils
{
    public static string GenerateVoiceID()
    {
        Console.WriteLine("GenerateVoiceID function ran");
        return $"VOICE_{Guid.NewGuid().ToString("N")[..12]}";
    }
}