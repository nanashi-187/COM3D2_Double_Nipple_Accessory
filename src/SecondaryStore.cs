using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;

namespace COM3D2.DoubleNipple
{
    // Persists the secondary accessory filename per maid in the plugin's BepInEx config.
    // Entries are only bound for maids that actually have a secondary, so looking up a maid
    // without one never adds a key to the config file.
    internal static class SecondaryStore
    {
        private const string Section = "Secondary Accessories";
        private const string Description = "Saved second nipple accessory menu filename.";

        private static readonly Dictionary<string, ConfigEntry<string>> Entries = new Dictionary<string, ConfigEntry<string>>();
        private static ConfigFile _config;

        // Fallback when BepInEx internals are unavailable: bind on every lookup, like 0.6.x did.
        private static bool _bindOnRead;

        public static void Init(ConfigFile config)
        {
            _config = config;
            var orphans = ReadOrphanedEntries(config);
            if (orphans == null)
            {
                _bindOnRead = true;
                Log.Warning("Could not read unbound config entries; saved secondaries will be bound on lookup.");
                return;
            }

            var saveOnSet = config.SaveOnConfigSet;
            config.SaveOnConfigSet = false;
            var removed = 0;
            try
            {
                foreach (var definition in new List<ConfigDefinition>(orphans.Keys))
                {
                    if (definition.Section != Section)
                    {
                        continue;
                    }

                    var raw = orphans[definition];
                    if (string.IsNullOrEmpty(raw) || raw.Trim().Length == 0)
                    {
                        orphans.Remove(definition);
                        removed++;
                        continue;
                    }

                    Entries[definition.Key] = config.Bind(Section, definition.Key, "", Description);
                }
            }
            finally
            {
                config.SaveOnConfigSet = saveOnSet;
            }

            if (removed > 0)
            {
                config.Save();
                Log.Info("Removed " + removed + " empty secondary entries from the config file.");
            }
        }

        public static string Get(Maid maid)
        {
            if (_config == null || maid == null)
            {
                return null;
            }

            var key = Slots.MaidKey(maid);
            ConfigEntry<string> entry;
            if (Entries.TryGetValue(key, out entry))
            {
                return entry.Value;
            }

            return _bindOnRead ? _config.Bind(Section, key, "", Description).Value : null;
        }

        public static bool Has(Maid maid)
        {
            return !string.IsNullOrEmpty(Get(maid));
        }

        public static void Set(Maid maid, string filename)
        {
            if (_config == null || maid == null || string.IsNullOrEmpty(filename))
            {
                return;
            }

            var key = Slots.MaidKey(maid);
            ConfigEntry<string> entry;
            if (!Entries.TryGetValue(key, out entry))
            {
                entry = _config.Bind(Section, key, "", Description);
                Entries[key] = entry;
            }

            entry.Value = filename;
            _config.Save();
        }

        public static void Clear(Maid maid)
        {
            if (_config == null || maid == null)
            {
                return;
            }

            var key = Slots.MaidKey(maid);
            ConfigEntry<string> entry;
            if (Entries.TryGetValue(key, out entry))
            {
                Entries.Remove(key);
                _config.Remove(entry.Definition);
                _config.Save();
            }
            else if (_bindOnRead)
            {
                _config.Bind(Section, key, "", Description).Value = "";
                _config.Save();
            }
        }

        private static Dictionary<ConfigDefinition, string> ReadOrphanedEntries(ConfigFile config)
        {
            try
            {
                var property = AccessTools.Property(typeof(ConfigFile), "OrphanedEntries");
                return property != null ? property.GetValue(config, null) as Dictionary<ConfigDefinition, string> : null;
            }
            catch (System.Exception ex)
            {
                Log.Warning("Reading OrphanedEntries failed: " + ex.Message);
                return null;
            }
        }
    }
}
