using System;
using System.Collections.Generic;

namespace WorldServer.networking
{
    public class VoicePrioritySettings
    {
        public bool EnablePriority { get; set; } = false;
        public int MaxPriorityPlayers { get; set; } = 10; // 10/20/30/40/50 slots
        public float PriorityVolume { get; set; } = 1.0f; // 100% volume for priority players
        public float NonPriorityVolume { get; set; } = 0.2f; // 20% volume (80% reduction)
        public bool GuildMembersGetPriority { get; set; } = true;
        public bool LockedPlayersGetPriority { get; set; } = true;
        public HashSet<int> ManualPriorityList { get; set; } = new HashSet<int>();

        public VoicePrioritySettings()
        {
            // Default constructor with sensible defaults
        }

        // Helper method to check if a player is in manual priority list
        public bool HasManualPriority(int accountId)
        {
            return ManualPriorityList.Contains(accountId);
        }

        // Helper method to add player to manual priority (with validation)
        public bool AddManualPriority(int accountId)
        {
            if (ManualPriorityList.Count >= MaxPriorityPlayers)
                return false; // Priority list full

            ManualPriorityList.Add(accountId);
            return true;
        }

        // Helper method to remove player from manual priority
        public bool RemoveManualPriority(int accountId)
        {
            return ManualPriorityList.Remove(accountId);
        }

        // Get current manual priority count
        public int GetManualPriorityCount()
        {
            return ManualPriorityList.Count;
        }

        // Clone settings (useful for database operations)
        public VoicePrioritySettings Clone()
        {
            return new VoicePrioritySettings
            {
                EnablePriority = this.EnablePriority,
                MaxPriorityPlayers = this.MaxPriorityPlayers,
                PriorityVolume = this.PriorityVolume,
                NonPriorityVolume = this.NonPriorityVolume,
                GuildMembersGetPriority = this.GuildMembersGetPriority,
                LockedPlayersGetPriority = this.LockedPlayersGetPriority,
                ManualPriorityList = new HashSet<int>(this.ManualPriorityList)
            };
        }

        // Validate settings (ensure they're within reasonable bounds)
        public void ValidateSettings()
        {
            // Ensure max players is within reasonable range
            if (MaxPriorityPlayers < 5) MaxPriorityPlayers = 5;
            if (MaxPriorityPlayers > 50) MaxPriorityPlayers = 50;

            // Ensure priority volume is between 0% and 200% (allow boosting)
            if (PriorityVolume < 0.0f) PriorityVolume = 0.0f;
            if (PriorityVolume > 2.0f) PriorityVolume = 2.0f;

            // Ensure non-priority volume is between 0% and 100%
            if (NonPriorityVolume < 0.0f) NonPriorityVolume = 0.0f;
            if (NonPriorityVolume > 1.0f) NonPriorityVolume = 1.0f;

            // Trim manual priority list if it exceeds max
            while (ManualPriorityList.Count > MaxPriorityPlayers)
            {
                // Remove oldest entries (this is a simplification)
                var enumerator = ManualPriorityList.GetEnumerator();
                enumerator.MoveNext();
                ManualPriorityList.Remove(enumerator.Current);
            }
        }
    }

    // Extension methods to make priority operations cleaner
    public static class VoicePriorityExtensions
    {
        public static bool IsAtMaxCapacity(this VoicePrioritySettings settings)
        {
            return settings.ManualPriorityList.Count >= settings.MaxPriorityPlayers;
        }

        public static int GetAvailableSlots(this VoicePrioritySettings settings)
        {
            return Math.Max(0, settings.MaxPriorityPlayers - settings.ManualPriorityList.Count);
        }

        public static float GetVolumeMultiplier(this VoicePrioritySettings settings, bool hasPriority)
        {
            return hasPriority ? settings.PriorityVolume : settings.NonPriorityVolume;
        }
    }
}