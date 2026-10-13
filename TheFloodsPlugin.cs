using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace TheFloods
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class TheFloodsPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "marc.thefloods";
        public const string PluginName = "The Floods";
        public const string PluginVersion = "0.9.2";

        internal static TheFloodsPlugin Instance;

        private FloodConfig _config;
        private FloodDirector _director;
        private FloodVisuals _visuals;
        private FloodWaterAdapter _waterAdapter;
        private FloodEnvironmentAdapter _environmentAdapter;
        private bool _rpcRegistered;
        private bool _loggedReady;

        internal void ReapplyWaterAfterNativeUpdate(WaterVolume volume)
        {
            if (_waterAdapter != null)
            {
                _waterAdapter.ReapplyAfterNativeUpdate(volume);
            }
        }

        private void LateUpdate()
        {
            try
            {
                if (_waterAdapter != null)
                {
                    // Water tiles can be reset after normal Update. Reapply the
                    // flood state after gameplay updates, before cameras render.
                    _waterAdapter.ReapplyForFrame();
                    _waterAdapter.SyncPhysicsIfNeeded();
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("The Floods late water sync warning: " + ex.Message);
            }
        }

        private void OnEnable()
        {
            Camera.onPreCull += OnCameraPreCull;
        }

        private void OnDisable()
        {
            Camera.onPreCull -= OnCameraPreCull;
        }

        private void OnCameraPreCull(Camera camera)
        {
            try
            {
                if (_waterAdapter != null)
                {
                    // This is the last safe point before the water meshes are
                    // rendered. It keeps visible water at the same height as
                    // WaterVolume.GetWaterSurface without changing terrain.
                    _waterAdapter.ReapplyVisualsForRender();
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("The Floods camera water sync warning: " + ex.Message);
            }
        }

        private void Awake()
        {
            Instance = this;
            _config = new FloodConfig(Config);
            _waterAdapter = new FloodWaterAdapter(Logger, _config);
            _environmentAdapter = new FloodEnvironmentAdapter(Logger, _config);
            _visuals = new FloodVisuals(_config);
            _director = new FloodDirector(Logger, _config, _waterAdapter, _environmentAdapter, _visuals);

            Harmony harmony = new Harmony(PluginGuid);
            harmony.PatchAll(typeof(TheFloodsPlugin).Assembly);

            Logger.LogInfo(PluginName + " " + PluginVersion + ": loaded. Waiting for world network.");
        }

        private void Update()
        {
            try
            {
                if (!_rpcRegistered && ZRoutedRpc.instance != null)
                {
                    ZRoutedRpc.instance.Register<string>(FloodDirector.RpcState, ReceiveStateRpc);
                    ZRoutedRpc.instance.Register<string>(FloodDirector.RpcMessage, ReceiveMessageRpc);
                    ZRoutedRpc.instance.Register<string>(FloodDirector.RpcStrike, ReceiveStrikeRpc);
                    _rpcRegistered = true;
                    Logger.LogInfo("The Floods: routed RPC handlers registered.");
                }

                _director.Tick();

                if (!_loggedReady && _director.IsWorldReady)
                {
                    _loggedReady = true;
                    Logger.LogInfo("The Floods: world controller ready. Type 'floods help' in F5 for testing controls.");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("The Floods Update error: " + ex);
            }
        }

        private void OnGUI()
        {
            try
            {
                // Unity invokes OnGUI more than once per frame for layout and repaint.
                // Draw only during repaint so a transparent storm tint is not stacked twice.
                if (Event.current == null || Event.current.type != EventType.Repaint)
                {
                    return;
                }

                _visuals.Draw(_director.CurrentState, _director.CurrentStormStrength, _director.CurrentWildfireIntensity, _config);
            }
            catch (Exception ex)
            {
                Logger.LogWarning("The Floods sky overlay error: " + ex.Message);
            }
        }

        private void OnDestroy()
        {
            try
            {
                _waterAdapter.Dispose();
                if (_director != null)
                {
                    _director.Dispose();
                }

                _environmentAdapter.ReleaseForcedEnvironment();
                _visuals.Dispose();
            }
            catch (Exception ex)
            {
                Logger.LogWarning("The Floods cleanup warning: " + ex.Message);
            }
            finally
            {
                Instance = null;
            }
        }

        private static void ReceiveStateRpc(long sender, string payload)
        {
            if (Instance == null)
            {
                return;
            }

            Instance._director.ReceiveState(payload);
        }

        private static void ReceiveMessageRpc(long sender, string message)
        {
            if (MessageHud.instance != null && !string.IsNullOrWhiteSpace(message))
            {
                MessageHud.instance.ShowMessage(MessageHud.MessageType.Center, message);
            }
        }

        private static void ReceiveStrikeRpc(long sender, string payload)
        {
            if (Instance == null)
            {
                return;
            }

            Instance._director.ReceiveStrike(payload);
        }

        internal bool TryHandleConsoleCommand(Terminal terminal)
        {
            string line = TerminalReflection.ReadInput(terminal);
            if (string.IsNullOrWhiteSpace(line))
            {
                return false;
            }

            string[] parts = line.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || !parts[0].Equals("floods", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            TerminalReflection.ClearInput(terminal);

            if (!_director.IsAuthoritative)
            {
                TerminalReflection.Write(terminal, "The Floods: commands must be run by the world host/server.");
                return true;
            }

            string reply = _director.HandleCommand(parts.Skip(1).ToArray());
            TerminalReflection.Write(terminal, reply);
            return true;
        }
    }

    internal sealed class FloodEventProfile
    {
        internal readonly FloodEventType Type;
        internal readonly string DisplayName;
        internal readonly ConfigEntry<bool> Enabled;
        // Retained for smooth upgrades from V0.6/V0.6.1. Named events now use
        // MinimumHeightMeters and MaximumHeightMeters when a new event starts.
        internal readonly ConfigEntry<float> HeightMeters;
        internal readonly ConfigEntry<float> MinimumHeightMeters;
        internal readonly ConfigEntry<float> MaximumHeightMeters;
        internal readonly ConfigEntry<float> OmenDays;
        internal readonly ConfigEntry<float> RisingDays;
        internal readonly ConfigEntry<float> PeakDays;
        internal readonly ConfigEntry<float> RecedingDays;
        internal readonly ConfigEntry<int> CooldownDays;
        internal readonly ConfigEntry<float> DailyChance;

        internal FloodEventProfile(
            ConfigFile config,
            FloodEventType type,
            string section,
            string displayName,
            float defaultHeight,
            float defaultMinimumHeight,
            float defaultMaximumHeight,
            float defaultOmenDays,
            float defaultRisingDays,
            float defaultPeakDays,
            float defaultRecedingDays,
            int defaultCooldownDays,
            float defaultDailyChance)
        {
            Type = type;
            DisplayName = displayName;
            Enabled = config.Bind(section, "Enabled", true, "Allow this event to occur naturally.");
            HeightMeters = config.Bind(section, "HeightMeters", defaultHeight, "Legacy nominal height retained for older configs. Named events use MinimumHeightMeters and MaximumHeightMeters.");
            MinimumHeightMeters = config.Bind(section, "MinimumHeightMeters", defaultMinimumHeight, "Lowest possible sea-level surge when this event begins. The exact height is rolled once and saved with the event.");
            MaximumHeightMeters = config.Bind(section, "MaximumHeightMeters", defaultMaximumHeight, "Highest possible sea-level surge when this event begins. The exact height is rolled once and saved with the event.");
            OmenDays = config.Bind(section, "OmenDays", defaultOmenDays, "How long the distant warning phase lasts in Valheim days.");
            RisingDays = config.Bind(section, "RisingDays", defaultRisingDays, "How long the water takes to reach its height in Valheim days.");
            PeakDays = config.Bind(section, "PeakDays", defaultPeakDays, "How long the event remains at full strength in Valheim days.");
            RecedingDays = config.Bind(section, "RecedingDays", defaultRecedingDays, "How long the water takes to return in Valheim days.");
            CooldownDays = config.Bind(section, "CooldownDays", defaultCooldownDays, "Minimum completed in-game days before this event can occur naturally again.");
            DailyChance = config.Bind(section, "DailyChance", defaultDailyChance, "Natural chance for this event on each eligible in-game day. 0.01 = 1%.");
        }

        internal float GetMinimumHeight()
        {
            return Mathf.Min(MinimumHeightMeters.Value, MaximumHeightMeters.Value);
        }

        internal float GetMaximumHeight()
        {
            return Mathf.Max(MinimumHeightMeters.Value, MaximumHeightMeters.Value);
        }
    }

    internal sealed class FloodConfig
    {
        internal readonly ConfigEntry<bool> Enabled;
        internal readonly ConfigEntry<int> MinimumWorldDay;
        internal readonly ConfigEntry<float> GameDaySeconds;

        // Kept as the custom/debug profile so existing test configs still work.
        internal readonly ConfigEntry<float> OmenDays;
        internal readonly ConfigEntry<float> RisingDays;
        internal readonly ConfigEntry<float> PeakDays;
        internal readonly ConfigEntry<float> RecedingDays;
        internal readonly ConfigEntry<float> MaximumSurgeMeters;
        internal readonly ConfigEntry<float> DebugMaximumSurgeMeters;

        internal readonly FloodEventProfile StormTide;
        internal readonly FloodEventProfile FlashSurge;
        internal readonly FloodEventProfile GreatFlood;
        internal readonly FloodEventProfile Drought;
        internal readonly FloodEventProfile Wildfire;

        // V0.8 uses a saved, hidden water-cycle scheduler rather than a visible
        // once-per-dawn dice roll. These values are real active-world hours:
        // server time advances while the world is running, and pauses with it.
        internal readonly ConfigEntry<float> FirstOrdinaryEventMinimumHours;
        internal readonly ConfigEntry<float> FirstOrdinaryEventMaximumHours;
        internal readonly ConfigEntry<float> OrdinaryRecoveryMinimumHours;
        internal readonly ConfigEntry<float> OrdinaryRecoveryMaximumHours;
        internal readonly ConfigEntry<float> FlashSurgeRecoveryMinimumHours;
        internal readonly ConfigEntry<float> FlashSurgeRecoveryMaximumHours;
        internal readonly ConfigEntry<float> GreatFloodRecoveryMinimumHours;
        internal readonly ConfigEntry<float> GreatFloodRecoveryMaximumHours;
        internal readonly ConfigEntry<float> StormTideWeight;
        internal readonly ConfigEntry<float> DroughtWeight;
        internal readonly ConfigEntry<float> FlashSurgeWeight;
        internal readonly ConfigEntry<float> GreatFloodFirstEligibleWorldHours;
        internal readonly ConfigEntry<float> GreatFloodFirstTargetMinimumWorldHours;
        internal readonly ConfigEntry<float> GreatFloodFirstTargetMaximumWorldHours;
        internal readonly ConfigEntry<float> GreatFloodFirstForceByWorldHours;
        internal readonly ConfigEntry<float> GreatFloodRepeatMinimumHours;
        internal readonly ConfigEntry<float> GreatFloodRepeatMaximumHours;
        internal readonly ConfigEntry<float> GreatFloodRepeatForceByHours;

        // Standard droughts use the Drought profile's normal 3–5m range.
        // Severe droughts use this separate 5–7.5m range and stay rare.
        internal readonly ConfigEntry<float> SevereDroughtChance;
        internal readonly ConfigEntry<float> SevereDroughtMinimumMeters;
        internal readonly ConfigEntry<float> SevereDroughtMaximumMeters;

        // A Flash Surge can begin with the sea pulling back, just like a
        // tsunami-style warning. The drawdown is rolled once and saved.
        internal readonly ConfigEntry<bool> EnableFlashSurgeDrawdown;
        internal readonly ConfigEntry<float> FlashSurgeDrawdownMinimumMeters;
        internal readonly ConfigEntry<float> FlashSurgeDrawdownMaximumMeters;

        internal readonly ConfigEntry<bool> EnableLiveWaterLevel;
        internal readonly ConfigEntry<bool> EnableWaterSurfaceQueryPatch;
        internal readonly ConfigEntry<bool> EnableOceanRendererLift;
        internal readonly ConfigEntry<float> OceanRendererMinimumSpan;
        internal readonly ConfigEntry<float> WaterSurfaceScanSeconds;

        internal readonly ConfigEntry<bool> EnableNativeThunderstorm;
        internal readonly ConfigEntry<string> NativeStormEnvironment;
        internal readonly ConfigEntry<float> NativeStormStartStrength;
        internal readonly ConfigEntry<float> SkyDarkening;
        internal readonly ConfigEntry<bool> EnableBlackHorizonStormBank;
        internal readonly ConfigEntry<float> HorizonStormBankOpacity;
        internal readonly ConfigEntry<float> HorizonStormBankHeight;
        internal readonly ConfigEntry<bool> EnableDistantLightningFlashes;
        internal readonly ConfigEntry<float> WildfireSkyTintStrength;

        internal readonly ConfigEntry<bool> EnableLightningStrikes;
        internal readonly ConfigEntry<bool> EnableAmbientBolts;
        internal readonly ConfigEntry<float> PlayerStrikeMinStrength;
        internal readonly ConfigEntry<float> StrikeGapMinSeconds;
        internal readonly ConfigEntry<float> StrikeGapMaxSeconds;
        internal readonly ConfigEntry<float> DirectHitChance;
        internal readonly ConfigEntry<float> StrikeGraceSeconds;
        internal readonly ConfigEntry<float> MinLightningDamage;
        internal readonly ConfigEntry<float> MaxLightningDamage;
        internal readonly ConfigEntry<float> WetDamageMultiplier;
        internal readonly ConfigEntry<float> LightningPushForce;
        internal readonly ConfigEntry<bool> RequireExposed;
        internal readonly ConfigEntry<bool> NonLethalLightning;
        internal readonly ConfigEntry<float> BoltLightIntensity;
        internal readonly ConfigEntry<float> BoltLightRange;
        internal readonly ConfigEntry<float> BoltDurationSeconds;
        internal readonly ConfigEntry<float> AmbientBoltsPerMinuteAtPeak;
        internal readonly ConfigEntry<string> RainEnvironmentNames;

        internal readonly ConfigEntry<float> DrynessRisePerHour;
        internal readonly ConfigEntry<float> DrynessRiseDroughtMultiplier;
        internal readonly ConfigEntry<float> DrynessRainResetSeconds;
        internal readonly ConfigEntry<float> RainWetStormStrength;

        internal readonly ConfigEntry<float> WildfireWeight;
        internal readonly ConfigEntry<float> SpontaneousDrynessThreshold;
        internal readonly ConfigEntry<float> SpontaneousPerWindowChance;
        internal readonly ConfigEntry<float> SpontaneousWindowMinimumHours;
        internal readonly ConfigEntry<float> SpontaneousWindowMaximumHours;
        internal readonly ConfigEntry<float> LightningIgnitionChance;
        internal readonly ConfigEntry<float> IgnitionMinRadius;
        internal readonly ConfigEntry<float> IgnitionMaxRadius;
        internal readonly ConfigEntry<int> MaxFireNodes;
        internal readonly ConfigEntry<float> SpreadIntervalSeconds;
        internal readonly ConfigEntry<float> SpreadChance;
        internal readonly ConfigEntry<float> SpreadStepMeters;
        internal readonly ConfigEntry<float> NodeLifeSeconds;
        internal readonly ConfigEntry<float> NodeRadius;
        internal readonly ConfigEntry<float> PlayerFireDamagePerTick;
        internal readonly ConfigEntry<float> FireDamageIntervalSeconds;
        internal readonly ConfigEntry<bool> DamageStructures;
        internal readonly ConfigEntry<float> DownwindBias;
        internal readonly ConfigEntry<string> WildfireForcedEnvironment;
        internal readonly ConfigEntry<float> MeadowsFlammability;
        internal readonly ConfigEntry<float> BlackForestFlammability;
        internal readonly ConfigEntry<float> PlainsFlammability;
        internal readonly ConfigEntry<float> SwampFlammability;
        internal readonly ConfigEntry<float> MountainFlammability;
        internal readonly ConfigEntry<float> MistlandsFlammability;
        internal readonly ConfigEntry<float> AshlandsFlammability;
        internal readonly ConfigEntry<float> DeepNorthFlammability;
        internal readonly ConfigEntry<float> OtherBiomeFlammability;

        internal readonly ConfigEntry<bool> DebugLogging;
        internal readonly ConfigEntry<bool> VerboseWaterTileBindingLogs;
        internal readonly ConfigEntry<bool> StartTestEventOnWorldLoad;
        internal readonly ConfigEntry<string> TestStartPhase;

        internal FloodConfig(ConfigFile config)
        {
            Enabled = config.Bind("General", "Enabled", true, "Master switch for The Floods.");
            MinimumWorldDay = config.Bind("Event Rules", "MinimumWorldDay", 6, "No natural water event can occur before this Valheim day. V0.8 defaults to day 6 so an ordinary event can arrive during an active early world without appearing on day one.");
            GameDaySeconds = config.Bind("Timeline", "GameDaySecondsFallback", 1800.0f, "Fallback full Valheim day length used only if the game day API is unavailable.");

            OmenDays = config.Bind("Timeline", "OmenDays", 0.5f, "Custom/debug event omen duration in Valheim days.");
            RisingDays = config.Bind("Timeline", "RisingDays", 2.0f, "Custom/debug event rise duration in Valheim days.");
            PeakDays = config.Bind("Timeline", "PeakDays", 2.0f, "Custom/debug event peak duration in Valheim days.");
            RecedingDays = config.Bind("Timeline", "RecedingDays", 2.0f, "Custom/debug event receding duration in Valheim days.");
            MaximumSurgeMeters = config.Bind("Flood Height", "MaximumSurgeMeters", 3.5f, "Height used by legacy floods start and debug test events.");
            DebugMaximumSurgeMeters = config.Bind("Flood Height", "DebugMaximumSurgeMeters", 20f, "Safety cap for floods set <metres>.");

            // Every named event rolls one height from its range when it begins.
            // The roll is saved in FloodState.PeakMeters so it stays identical for
            // every player, survives reloads, and never changes mid-storm.
            // Durations remain intentionally readable: a player sees the warning,
            // has time to react, then watches the water arrive and settle.
            StormTide = new FloodEventProfile(config, FloodEventType.StormTide, "Storm Tide", "Storm Tide", 2.5f, 1.5f, 5.0f, 0.066667f, 0.166667f, 0.20f, 0.20f, 7, 0.075f);
            FlashSurge = new FloodEventProfile(config, FloodEventType.FlashSurge, "Flash Surge", "Flash Surge", 10f, 6.0f, 12.0f, 0.035f, 0.066667f, 0.10f, 0.233333f, 20, 0.028f);
            GreatFlood = new FloodEventProfile(config, FloodEventType.GreatFlood, "The Great Flood", "The Great Flood", 12f, 8.0f, 16.0f, 0.233333f, 0.60f, 0.40f, 0.60f, 55, 0.0075f);

            // Standard drought: -3m to -5m. The separate severe range below
            // occasionally rolls -5m to -7.5m from the exact same proven
            // signed WaterVolume route, so no terrain is edited.
            Drought = new FloodEventProfile(config, FloodEventType.Drought, "Drought", "Drought", 4.0f, 3.0f, 5.0f, 0.10f, 0.30f, 0.40f, 0.30f, 10, 0.12f);
            Wildfire = new FloodEventProfile(config, FloodEventType.Wildfire, "Wildfire", "Wildfire", 0f, 0f, 0f, 0.08f, 0.18f, 0.28f, 0.22f, 12, 0.02f);

            FirstOrdinaryEventMinimumHours = config.Bind("Water Cycle Scheduler", "FirstOrdinaryEventMinimumHours", 0.75f, "After MinimumWorldDay, the first ordinary event begins after a hidden random delay in this range. Hours are active server/world hours.");
            FirstOrdinaryEventMaximumHours = config.Bind("Water Cycle Scheduler", "FirstOrdinaryEventMaximumHours", 2.0f, "Largest hidden delay after MinimumWorldDay before the first ordinary event can begin.");
            OrdinaryRecoveryMinimumHours = config.Bind("Water Cycle Scheduler", "OrdinaryRecoveryMinimumHours", 1.5f, "Random quiet-world minimum after a Storm Tide or Drought ends before another ordinary event can begin.");
            OrdinaryRecoveryMaximumHours = config.Bind("Water Cycle Scheduler", "OrdinaryRecoveryMaximumHours", 3.5f, "Random quiet-world maximum after a Storm Tide or Drought ends before another ordinary event can begin.");
            FlashSurgeRecoveryMinimumHours = config.Bind("Water Cycle Scheduler", "FlashSurgeRecoveryMinimumHours", 2.5f, "Random quiet-world minimum after a Flash Surge ends before another ordinary event can begin.");
            FlashSurgeRecoveryMaximumHours = config.Bind("Water Cycle Scheduler", "FlashSurgeRecoveryMaximumHours", 5.0f, "Random quiet-world maximum after a Flash Surge ends before another ordinary event can begin.");
            GreatFloodRecoveryMinimumHours = config.Bind("Water Cycle Scheduler", "GreatFloodRecoveryMinimumHours", 6.0f, "Random quiet-world minimum after the Great Flood ends before any natural water event can begin.");
            GreatFloodRecoveryMaximumHours = config.Bind("Water Cycle Scheduler", "GreatFloodRecoveryMaximumHours", 10.0f, "Random quiet-world maximum after the Great Flood ends before any natural water event can begin.");
            StormTideWeight = config.Bind("Water Cycle Scheduler", "StormTideWeight", 45f, "Relative chance that the next ordinary water event is a Storm Tide.");
            DroughtWeight = config.Bind("Water Cycle Scheduler", "DroughtWeight", 40f, "Relative chance that the next ordinary water event is a Drought.");
            FlashSurgeWeight = config.Bind("Water Cycle Scheduler", "FlashSurgeWeight", 15f, "Relative chance that the next ordinary water event is a Flash Surge.");
            GreatFloodFirstEligibleWorldHours = config.Bind("Water Cycle Scheduler", "GreatFloodFirstEligibleWorldHours", 8.0f, "The Great Flood cannot naturally occur before this much total active world age.");
            GreatFloodFirstTargetMinimumWorldHours = config.Bind("Water Cycle Scheduler", "GreatFloodFirstTargetMinimumWorldHours", 15.0f, "Earliest target age for a world's first Great Flood. The exact target is hidden and randomized.");
            GreatFloodFirstTargetMaximumWorldHours = config.Bind("Water Cycle Scheduler", "GreatFloodFirstTargetMaximumWorldHours", 25.0f, "Latest target age for a world's first Great Flood. The exact target is hidden and randomized.");
            GreatFloodFirstForceByWorldHours = config.Bind("Water Cycle Scheduler", "GreatFloodFirstForceByWorldHours", 32.0f, "Safety limit: if a mature world still has not seen its first Great Flood, the scheduler waits for the next safe quiet window and begins one by this world age.");
            GreatFloodRepeatMinimumHours = config.Bind("Water Cycle Scheduler", "GreatFloodRepeatMinimumHours", 35.0f, "Minimum active-world hours before another Great Flood can be scheduled after one completes.");
            GreatFloodRepeatMaximumHours = config.Bind("Water Cycle Scheduler", "GreatFloodRepeatMaximumHours", 60.0f, "Maximum randomized active-world hours before another Great Flood is scheduled after one completes.");
            GreatFloodRepeatForceByHours = config.Bind("Water Cycle Scheduler", "GreatFloodRepeatForceByHours", 72.0f, "Safety limit after a Great Flood: the next one will be allowed at the first safe quiet window by this many active-world hours later.");
            SevereDroughtChance = config.Bind("Drought", "SevereDroughtChance", 0.20f, "Chance that a naturally scheduled Drought becomes a Severe Drought instead of the normal 3–5m retreat. 0.20 means 20%.");
            SevereDroughtMinimumMeters = config.Bind("Drought", "SevereDroughtMinimumMeters", 5.0f, "Smallest sea retreat for a Severe Drought.");
            SevereDroughtMaximumMeters = config.Bind("Drought", "SevereDroughtMaximumMeters", 7.5f, "Largest sea retreat for a Severe Drought.");

            // V0.8 no longer uses the old per-dawn DailyChance/CooldownDays
            // settings. They are removed from old configs so the scheduler has
            // one clear source of truth.
            RemoveLegacyDailyRollSettings(config);

            EnableFlashSurgeDrawdown = config.Bind("Flash Surge", "EnableDrawdown", true, "Before a Flash Surge, the sea can briefly pull back as a warning.");
            FlashSurgeDrawdownMinimumMeters = config.Bind("Flash Surge", "DrawdownMinimumMeters", 1.0f, "Smallest pre-surge sea retreat in metres. Used only when EnableDrawdown is true.");
            FlashSurgeDrawdownMaximumMeters = config.Bind("Flash Surge", "DrawdownMaximumMeters", 3.0f, "Largest pre-surge sea retreat in metres. Used only when EnableDrawdown is true.");

            EnableLiveWaterLevel = config.Bind("Flood Height", "EnableLiveWaterLevel", true, "Master switch for the live flood-water adapter. Use a copied test world first.");
            EnableWaterSurfaceQueryPatch = config.Bind("Flood Height", "EnableWaterSurfaceQueryPatch", true, "Uses Valheim WaterVolume terrain-water offsets for live flood physics without editing terrain.");
            EnableOceanRendererLift = config.Bind("Flood Height", "EnableOceanRendererLift", true, "Raises terrain-water mesh children so the shoreline visibly floods. Does not move terrain.");
            OceanRendererMinimumSpan = config.Bind("Flood Height", "OceanRendererMinimumSpan", 256f, "Legacy compatibility setting. Ocean tiles are identified through their linked Heightmap.");
            WaterSurfaceScanSeconds = config.Bind("Flood Height", "WaterSurfaceScanSeconds", 3f, "How often the mod scans for newly loaded terrain water tiles.");

            EnableNativeThunderstorm = config.Bind("Storm Visuals", "EnableNativeThunderstorm", true, "Use Valheim's native thunderstorm during active flood phases.");
            NativeStormEnvironment = config.Bind("Storm Visuals", "NativeStormEnvironment", "ThunderStorm", "Internal Valheim environment name to force during active flood phases.");
            NativeStormStartStrength = config.Bind("Storm Visuals", "NativeStormStartStrength", 0.45f, "Storm strength at which native thunder, rain and lightning begin. The omen remains mostly distant and black before this.");
            SkyDarkening = config.Bind("Storm Visuals", "SkyDarkening", 0.72f, "Strength of the storm-darkening overlay. 0 disables it.");
            EnableBlackHorizonStormBank = config.Bind("Storm Visuals", "EnableBlackHorizonStormBank", true, "Adds a distant black shelf-cloud bank during the omen and flood.");
            HorizonStormBankOpacity = config.Bind("Storm Visuals", "HorizonStormBankOpacity", 0.88f, "Opacity of the distant black storm bank.");
            HorizonStormBankHeight = config.Bind("Storm Visuals", "HorizonStormBankHeight", 0.64f, "Vertical screen fraction occupied by the distant storm bank.");
            EnableDistantLightningFlashes = config.Bind("Storm Visuals", "EnableDistantLightningFlashes", true, "Adds subtle distant lightning flashes on top of Valheim's native thunderstorm.");
            WildfireSkyTintStrength = config.Bind("Storm Visuals", "WildfireSkyTintStrength", 0.52f, "Strength of the warm smoke tint during a wildfire event.");

            EnableLightningStrikes = config.Bind("Lightning", "EnableLightningStrikes", true, "Allows storms to create real ground lightning strikes. Direct strikes can damage and kill exposed players.");
            EnableAmbientBolts = config.Bind("Lightning", "EnableAmbientBolts", true, "Shows non-damaging procedural bolts around players during active storm phases.");
            PlayerStrikeMinStrength = config.Bind("Lightning", "PlayerStrikeMinStrength", 0.55f, "Minimum storm strength before dangerous player-targeting lightning is allowed.");
            StrikeGapMinSeconds = config.Bind("Lightning", "StrikeGapMinSeconds", 25f, "Shortest real-time gap between dangerous lightning checks at peak storm strength.");
            StrikeGapMaxSeconds = config.Bind("Lightning", "StrikeGapMaxSeconds", 90f, "Longest real-time gap between dangerous lightning checks at the edge of a storm.");
            DirectHitChance = config.Bind("Lightning", "DirectHitChance", 0.12f, "Chance that a dangerous strike targets an exposed player instead of a nearby miss.");
            StrikeGraceSeconds = config.Bind("Lightning", "StrikeGraceSeconds", 8f, "Minimum real-time grace period between damaging strikes on the same player.");
            MinLightningDamage = config.Bind("Lightning", "MinLightningDamage", 40f, "Lightning damage near the dangerous-storm threshold.");
            MaxLightningDamage = config.Bind("Lightning", "MaxLightningDamage", 140f, "Lightning damage at peak storm strength. This can kill low-health players.");
            WetDamageMultiplier = config.Bind("Lightning", "WetDamageMultiplier", 1.5f, "Damage multiplier for wet players struck by lightning.");
            LightningPushForce = config.Bind("Lightning", "LightningPushForce", 8f, "Knockback force applied by a direct lightning strike.");
            RequireExposed = config.Bind("Lightning", "RequireExposed", true, "Sheltered players are immune to direct lightning strikes.");
            NonLethalLightning = config.Bind("Lightning", "NonLethalLightning", false, "If true, direct lightning leaves the player at 1 HP instead of killing them.");
            BoltLightIntensity = config.Bind("Lightning", "BoltLightIntensity", 6f, "World light intensity for procedural lightning bolts. V0.9.1 keeps this modest and uses the screen flash for most brightness.");
            BoltLightRange = config.Bind("Lightning", "BoltLightRange", 55f, "World light range for procedural lightning bolts. Kept short to avoid bloom streaking.");
            BoltDurationSeconds = config.Bind("Lightning", "BoltDurationSeconds", 0.22f, "How long a procedural bolt remains visible.");
            AmbientBoltsPerMinuteAtPeak = config.Bind("Lightning", "AmbientBoltsPerMinuteAtPeak", 5f, "Approximate number of non-damaging ambient bolts per minute at peak storm strength.");
            RainEnvironmentNames = config.Bind("Lightning", "RainEnvironmentNames", "Rain,LightRain,ThunderStorm,SwampRain,Mistlands_rain,Mistlands_thunder,Ashrain,Ashlands_ashrain", "Comma-separated environment names that count as wet rain for lightning ignition and wildfire suppression.");

            DrynessRisePerHour = config.Bind("Dryness", "DrynessRisePerHour", 0.055f, "How much the saved dryness index rises per active world hour without rain.");
            DrynessRiseDroughtMultiplier = config.Bind("Dryness", "DrynessRiseDroughtMultiplier", 3.0f, "Multiplier for dryness gain during drought phases.");
            DrynessRainResetSeconds = config.Bind("Dryness", "DrynessRainResetSeconds", 900f, "Real active seconds of rain/storm needed to pull dryness from 1 toward 0.");
            RainWetStormStrength = config.Bind("Dryness", "RainWetStormStrength", 0.62f, "Storm strength at which rain environments make the ground wet enough to block ignition.");

            WildfireWeight = config.Bind("Wildfire", "WildfireWeight", 1.0f, "Reserved tuning weight for wildfire scheduling. Wildfires use their own dryness-driven schedule instead of the water-event pool.");
            SpontaneousDrynessThreshold = config.Bind("Wildfire", "SpontaneousDrynessThreshold", 0.70f, "Dryness index required before spontaneous wildfire ignition can be scheduled.");
            SpontaneousPerWindowChance = config.Bind("Wildfire", "SpontaneousPerWindowChance", 0.05f, "Chance per wildfire schedule window to ignite when dryness and biome fuel allow it.");
            SpontaneousWindowMinimumHours = config.Bind("Wildfire", "SpontaneousWindowMinimumHours", 1.0f, "Shortest active-world delay between spontaneous wildfire ignition checks.");
            SpontaneousWindowMaximumHours = config.Bind("Wildfire", "SpontaneousWindowMaximumHours", 3.0f, "Longest active-world delay between spontaneous wildfire ignition checks.");
            LightningIgnitionChance = config.Bind("Wildfire", "LightningIgnitionChance", 0.25f, "Base chance that a dry lightning strike ignites a wildfire, before dryness and biome flammability are applied.");
            IgnitionMinRadius = config.Bind("Wildfire", "IgnitionMinRadius", 40f, "Closest distance from a player that natural wildfire ignition may choose.");
            IgnitionMaxRadius = config.Bind("Wildfire", "IgnitionMaxRadius", 120f, "Farthest distance from a player that natural wildfire ignition may choose.");
            MaxFireNodes = config.Bind("Wildfire", "MaxFireNodes", 72, "Hard cap on simultaneous local fire nodes for performance. V0.9.1 uses many smaller nodes so the fire reads as a moving front.");
            
            // INCREASED FIRE SPREAD SPEED FIX
            SpreadIntervalSeconds = config.Bind("Wildfire", "SpreadIntervalSeconds", 0.9f, "How often each fire node can attempt to spread. Lowered to 0.9f to simulate character running speed.");
            
            SpreadChance = config.Bind("Wildfire", "SpreadChance", 0.48f, "Chance that a fire node spreads on each interval during active fire phases.");
            SpreadStepMeters = config.Bind("Wildfire", "SpreadStepMeters", 4.5f, "Average distance between parent and child fire nodes.");
            NodeLifeSeconds = config.Bind("Wildfire", "NodeLifeSeconds", 70f, "How long a fire node burns without rain, floodwater, or burnout suppression.");
            NodeRadius = config.Bind("Wildfire", "NodeRadius", 2.25f, "Radius around each node that shows fire and can damage the player.");
            PlayerFireDamagePerTick = config.Bind("Wildfire", "PlayerFireDamagePerTick", 8f, "Fire damage applied per tick when an unwet player stands in a fire node.");
            FireDamageIntervalSeconds = config.Bind("Wildfire", "FireDamageIntervalSeconds", 1.5f, "Real seconds between player fire-damage ticks.");
            DamageStructures = config.Bind("Wildfire", "DamageStructures", false, "Reserved safety valve for future structure/tree fire damage. The v0.9.0 implementation leaves structures untouched.");
            DownwindBias = config.Bind("Wildfire", "DownwindBias", 0.72f, "How strongly fire spread prefers the current wind direction. 0 is random, 1 is fully downwind.");
            WildfireForcedEnvironment = config.Bind("Wildfire", "WildfireForcedEnvironment", "", "Optional Valheim environment to force during wildfire. Empty leaves natural weather alone.");
            MeadowsFlammability = config.Bind("Wildfire Biomes", "MeadowsFlammability", 1.0f, "Ignition fuel multiplier for Meadows.");
            BlackForestFlammability = config.Bind("Wildfire Biomes", "BlackForestFlammability", 0.9f, "Ignition fuel multiplier for Black Forest.");
            PlainsFlammability = config.Bind("Wildfire Biomes", "PlainsFlammability", 1.0f, "Ignition fuel multiplier for Plains.");
            SwampFlammability = config.Bind("Wildfire Biomes", "SwampFlammability", 0.15f, "Ignition fuel multiplier for Swamp.");
            MountainFlammability = config.Bind("Wildfire Biomes", "MountainFlammability", 0.10f, "Ignition fuel multiplier for Mountain.");
            MistlandsFlammability = config.Bind("Wildfire Biomes", "MistlandsFlammability", 0.5f, "Ignition fuel multiplier for Mistlands.");
            AshlandsFlammability = config.Bind("Wildfire Biomes", "AshlandsFlammability", 0.0f, "Ignition fuel multiplier for Ashlands.");
            DeepNorthFlammability = config.Bind("Wildfire Biomes", "DeepNorthFlammability", 0.0f, "Ignition fuel multiplier for Deep North.");
            OtherBiomeFlammability = config.Bind("Wildfire Biomes", "OtherBiomeFlammability", 0.3f, "Ignition fuel multiplier for unknown or modded biomes.");

            DebugLogging = config.Bind("Debug", "DebugLogging", false, "Writes concise event/state diagnostics to BepInEx LogOutput.log.");
            VerboseWaterTileBindingLogs = config.Bind("Debug", "VerboseWaterTileBindingLogs", false, "Logs every individual terrain-water tile. Leave false for normal play.");
            StartTestEventOnWorldLoad = config.Bind("Debug", "StartTestEventOnWorldLoad", false, "Starts a custom test flood whenever the host loads a world. Turn off after testing.");
            TestStartPhase = config.Bind("Debug", "TestStartPhase", "Omen", "Custom test phase: Omen, Rising, Peak, or Receding.");
        }

        internal FloodEventProfile GetProfile(FloodEventType type)
        {
            switch (type)
            {
                case FloodEventType.StormTide:
                    return StormTide;
                case FloodEventType.FlashSurge:
                    return FlashSurge;
                case FloodEventType.GreatFlood:
                    return GreatFlood;
                case FloodEventType.Drought:
                    return Drought;
                case FloodEventType.Wildfire:
                    return Wildfire;
                default:
                    return null;
            }
        }

        internal float GetFlashSurgeDrawdownMinimum()
        {
            return Mathf.Min(FlashSurgeDrawdownMinimumMeters.Value, FlashSurgeDrawdownMaximumMeters.Value);
        }

        internal float GetFlashSurgeDrawdownMaximum()
        {
            return Mathf.Max(FlashSurgeDrawdownMinimumMeters.Value, FlashSurgeDrawdownMaximumMeters.Value);
        }

        private static void RemoveLegacyDailyRollSettings(ConfigFile config)
        {
            string[] sections = { "Storm Tide", "Flash Surge", "The Great Flood", "Drought" };
            for (int index = 0; index < sections.Length; index++)
            {
                config.Remove(new ConfigDefinition(sections[index], "CooldownDays"));
                config.Remove(new ConfigDefinition(sections[index], "DailyChance"));
            }
        }
    }

    internal enum FloodEventType
    {
        None = 0,
        Custom = 1,
        StormTide = 2,
        FlashSurge = 3,
        GreatFlood = 4,
        Drought = 5,
        Wildfire = 6
    }

    internal enum FloodPhase
    {
        Dormant = 0,
        Omen = 1,
        Rising = 2,
        Peak = 3,
        Receding = 4
    }

    [Serializable]
    internal sealed class FloodState
    {
        internal int FormatVersion = 5;
        internal FloodPhase Phase = FloodPhase.Dormant;
        internal FloodEventType EventType = FloodEventType.None;
        internal double PhaseStartedWorldSeconds;
        internal double PhaseEndsWorldSeconds;
        // Positive values raise the sea; negative values lower it for a Drought.
        internal float PeakMeters = 3.5f;
        // Flash Surge only: positive magnitude of the temporary pre-surge retreat.
        internal float DrawdownMeters;
        internal long EventNumber;
        internal long EventSeed;
        internal long LastCompletedDay = -99999;
        internal long LastRollDay = -99999;
        internal long LastStormTideDay = -99999;
        internal long LastFlashSurgeDay = -99999;
        internal long LastGreatFloodDay = -99999;
        internal long LastDroughtDay = -99999;
        internal bool ManualEvent;

        // V0.8 scheduler state. Exact next-event times are saved so quitting,
        // reloading, or a server restart cannot reshuffle the water cycle.
        internal bool IsSevereDrought;
        internal bool SchedulerInitialized;
        internal double NextOrdinaryEventWorldSeconds;
        internal double NextGreatFloodWorldSeconds;
        internal double GreatFloodForceWorldSeconds;
        internal double GlobalRecoveryEndsWorldSeconds;
        internal long SchedulerCycle;

        // V0.9 appends climate/fire state. Live wildfire node lists are not
        // serialized; an active wildfire is re-seeded from WildfireOrigin on load.
        internal float DrynessIndex;
        internal float WildfireOriginX;
        internal float WildfireOriginY;
        internal float WildfireOriginZ;
        internal double NextStrikeWorldSeconds;
        internal double LastDrynessUpdateWorldSeconds;
        internal double NextWildfireWorldSeconds;
        internal double WildfireForceWorldSeconds;

        internal string Serialize()
        {
            return string.Join("|", new[]
            {
                FormatVersion.ToString(CultureInfo.InvariantCulture),
                ((int)Phase).ToString(CultureInfo.InvariantCulture),
                PhaseStartedWorldSeconds.ToString("R", CultureInfo.InvariantCulture),
                PhaseEndsWorldSeconds.ToString("R", CultureInfo.InvariantCulture),
                PeakMeters.ToString("R", CultureInfo.InvariantCulture),
                DrawdownMeters.ToString("R", CultureInfo.InvariantCulture),
                EventNumber.ToString(CultureInfo.InvariantCulture),
                EventSeed.ToString(CultureInfo.InvariantCulture),
                LastCompletedDay.ToString(CultureInfo.InvariantCulture),
                LastRollDay.ToString(CultureInfo.InvariantCulture),
                ManualEvent ? "1" : "0",
                ((int)EventType).ToString(CultureInfo.InvariantCulture),
                LastStormTideDay.ToString(CultureInfo.InvariantCulture),
                LastFlashSurgeDay.ToString(CultureInfo.InvariantCulture),
                LastGreatFloodDay.ToString(CultureInfo.InvariantCulture),
                LastDroughtDay.ToString(CultureInfo.InvariantCulture),
                IsSevereDrought ? "1" : "0",
                SchedulerInitialized ? "1" : "0",
                NextOrdinaryEventWorldSeconds.ToString("R", CultureInfo.InvariantCulture),
                NextGreatFloodWorldSeconds.ToString("R", CultureInfo.InvariantCulture),
                GreatFloodForceWorldSeconds.ToString("R", CultureInfo.InvariantCulture),
                GlobalRecoveryEndsWorldSeconds.ToString("R", CultureInfo.InvariantCulture),
                SchedulerCycle.ToString(CultureInfo.InvariantCulture),
                DrynessIndex.ToString("R", CultureInfo.InvariantCulture),
                WildfireOriginX.ToString("R", CultureInfo.InvariantCulture),
                WildfireOriginY.ToString("R", CultureInfo.InvariantCulture),
                WildfireOriginZ.ToString("R", CultureInfo.InvariantCulture),
                NextStrikeWorldSeconds.ToString("R", CultureInfo.InvariantCulture),
                LastDrynessUpdateWorldSeconds.ToString("R", CultureInfo.InvariantCulture),
                NextWildfireWorldSeconds.ToString("R", CultureInfo.InvariantCulture),
                WildfireForceWorldSeconds.ToString("R", CultureInfo.InvariantCulture)
            });
        }

        internal static bool TryDeserialize(string data, out FloodState state)
        {
            state = null;
            if (string.IsNullOrWhiteSpace(data))
            {
                return false;
            }

            string[] p = data.Split('|');
            if (p.Length != 10 && p.Length != 14 && p.Length != 16 && p.Length != 23 && p.Length != 31)
            {
                return false;
            }

            try
            {
                state = new FloodState
                {
                    FormatVersion = int.Parse(p[0], CultureInfo.InvariantCulture),
                    Phase = (FloodPhase)int.Parse(p[1], CultureInfo.InvariantCulture),
                    PhaseStartedWorldSeconds = double.Parse(p[2], CultureInfo.InvariantCulture),
                    PhaseEndsWorldSeconds = double.Parse(p[3], CultureInfo.InvariantCulture),
                    PeakMeters = float.Parse(p[4], CultureInfo.InvariantCulture)
                };

                if (p.Length == 23 || p.Length == 31)
                {
                    state.DrawdownMeters = float.Parse(p[5], CultureInfo.InvariantCulture);
                    state.EventNumber = long.Parse(p[6], CultureInfo.InvariantCulture);
                    state.EventSeed = long.Parse(p[7], CultureInfo.InvariantCulture);
                    state.LastCompletedDay = long.Parse(p[8], CultureInfo.InvariantCulture);
                    state.LastRollDay = long.Parse(p[9], CultureInfo.InvariantCulture);
                    state.ManualEvent = p[10] == "1";
                    state.EventType = (FloodEventType)int.Parse(p[11], CultureInfo.InvariantCulture);
                    state.LastStormTideDay = long.Parse(p[12], CultureInfo.InvariantCulture);
                    state.LastFlashSurgeDay = long.Parse(p[13], CultureInfo.InvariantCulture);
                    state.LastGreatFloodDay = long.Parse(p[14], CultureInfo.InvariantCulture);
                    state.LastDroughtDay = long.Parse(p[15], CultureInfo.InvariantCulture);
                    state.IsSevereDrought = p[16] == "1";
                    state.SchedulerInitialized = p[17] == "1";
                    state.NextOrdinaryEventWorldSeconds = double.Parse(p[18], CultureInfo.InvariantCulture);
                    state.NextGreatFloodWorldSeconds = double.Parse(p[19], CultureInfo.InvariantCulture);
                    state.GreatFloodForceWorldSeconds = double.Parse(p[20], CultureInfo.InvariantCulture);
                    state.GlobalRecoveryEndsWorldSeconds = double.Parse(p[21], CultureInfo.InvariantCulture);
                    state.SchedulerCycle = long.Parse(p[22], CultureInfo.InvariantCulture);
                    if (p.Length == 31)
                    {
                        state.DrynessIndex = Mathf.Clamp01(float.Parse(p[23], CultureInfo.InvariantCulture));
                        state.WildfireOriginX = float.Parse(p[24], CultureInfo.InvariantCulture);
                        state.WildfireOriginY = float.Parse(p[25], CultureInfo.InvariantCulture);
                        state.WildfireOriginZ = float.Parse(p[26], CultureInfo.InvariantCulture);
                        state.NextStrikeWorldSeconds = double.Parse(p[27], CultureInfo.InvariantCulture);
                        state.LastDrynessUpdateWorldSeconds = double.Parse(p[28], CultureInfo.InvariantCulture);
                        state.NextWildfireWorldSeconds = double.Parse(p[29], CultureInfo.InvariantCulture);
                        state.WildfireForceWorldSeconds = double.Parse(p[30], CultureInfo.InvariantCulture);
                    }

                    state.FormatVersion = 5;
                }
                else if (p.Length == 16)
                {
                    state.DrawdownMeters = float.Parse(p[5], CultureInfo.InvariantCulture);
                    state.EventNumber = long.Parse(p[6], CultureInfo.InvariantCulture);
                    state.EventSeed = long.Parse(p[7], CultureInfo.InvariantCulture);
                    state.LastCompletedDay = long.Parse(p[8], CultureInfo.InvariantCulture);
                    state.LastRollDay = long.Parse(p[9], CultureInfo.InvariantCulture);
                    state.ManualEvent = p[10] == "1";
                    state.EventType = (FloodEventType)int.Parse(p[11], CultureInfo.InvariantCulture);
                    state.LastStormTideDay = long.Parse(p[12], CultureInfo.InvariantCulture);
                    state.LastFlashSurgeDay = long.Parse(p[13], CultureInfo.InvariantCulture);
                    state.LastGreatFloodDay = long.Parse(p[14], CultureInfo.InvariantCulture);
                    state.LastDroughtDay = long.Parse(p[15], CultureInfo.InvariantCulture);
                    state.FormatVersion = 5;
                }
                else if (p.Length == 14)
                {
                    state.EventNumber = long.Parse(p[5], CultureInfo.InvariantCulture);
                    state.EventSeed = long.Parse(p[6], CultureInfo.InvariantCulture);
                    state.LastCompletedDay = long.Parse(p[7], CultureInfo.InvariantCulture);
                    state.LastRollDay = long.Parse(p[8], CultureInfo.InvariantCulture);
                    state.ManualEvent = p[9] == "1";
                    state.EventType = (FloodEventType)int.Parse(p[10], CultureInfo.InvariantCulture);
                    state.LastStormTideDay = long.Parse(p[11], CultureInfo.InvariantCulture);
                    state.LastFlashSurgeDay = long.Parse(p[12], CultureInfo.InvariantCulture);
                    state.LastGreatFloodDay = long.Parse(p[13], CultureInfo.InvariantCulture);
                    state.LastDroughtDay = state.LastCompletedDay;
                    state.DrawdownMeters = 0f;
                    state.FormatVersion = 5;
                }
                else
                {
                    state.EventNumber = long.Parse(p[5], CultureInfo.InvariantCulture);
                    state.EventSeed = long.Parse(p[6], CultureInfo.InvariantCulture);
                    state.LastCompletedDay = long.Parse(p[7], CultureInfo.InvariantCulture);
                    state.LastRollDay = long.Parse(p[8], CultureInfo.InvariantCulture);
                    state.ManualEvent = p[9] == "1";
                    state.EventType = state.Phase == FloodPhase.Dormant ? FloodEventType.None : FloodEventType.Custom;
                    state.LastStormTideDay = state.LastCompletedDay;
                    state.LastFlashSurgeDay = state.LastCompletedDay;
                    state.LastGreatFloodDay = state.LastCompletedDay;
                    state.LastDroughtDay = state.LastCompletedDay;
                    state.DrawdownMeters = 0f;
                    state.FormatVersion = 5;
                }

                return true;
            }
            catch
            {
                state = null;
                return false;
            }
        }
    }

    internal sealed class LightningStrikePayload
    {
        internal Vector3 Position;
        internal float Damage;
        internal bool IsDirectHit;
        internal long TargetPlayerId;
        internal int Ignite;
        internal long Seed;

        internal string Serialize()
        {
            return string.Join("|", new[]
            {
                Position.x.ToString("R", CultureInfo.InvariantCulture),
                Position.y.ToString("R", CultureInfo.InvariantCulture),
                Position.z.ToString("R", CultureInfo.InvariantCulture),
                Damage.ToString("R", CultureInfo.InvariantCulture),
                IsDirectHit ? "1" : "0",
                TargetPlayerId.ToString(CultureInfo.InvariantCulture),
                Ignite.ToString(CultureInfo.InvariantCulture),
                Seed.ToString(CultureInfo.InvariantCulture)
            });
        }

        internal static bool TryDeserialize(string data, out LightningStrikePayload payload)
        {
            payload = null;
            if (string.IsNullOrWhiteSpace(data))
            {
                return false;
            }

            string[] parts = data.Split('|');
            if (parts.Length != 8)
            {
                return false;
            }

            try
            {
                payload = new LightningStrikePayload
                {
                    Position = new Vector3(
                        float.Parse(parts[0], CultureInfo.InvariantCulture),
                        float.Parse(parts[1], CultureInfo.InvariantCulture),
                        float.Parse(parts[2], CultureInfo.InvariantCulture)),
                    Damage = float.Parse(parts[3], CultureInfo.InvariantCulture),
                    IsDirectHit = parts[4] == "1",
                    TargetPlayerId = long.Parse(parts[5], CultureInfo.InvariantCulture),
                    Ignite = int.Parse(parts[6], CultureInfo.InvariantCulture),
                    Seed = long.Parse(parts[7], CultureInfo.InvariantCulture)
                };
                return true;
            }
            catch
            {
                payload = null;
                return false;
            }
        }
    }

    internal sealed class FloodStateStore
    {
        private readonly BepInEx.Logging.ManualLogSource _logger;
        private readonly string _folder;

        internal FloodStateStore(BepInEx.Logging.ManualLogSource logger)
        {
            _logger = logger;
            _folder = Path.Combine(Paths.ConfigPath, "marc.thefloods");
        }

        internal FloodState Load(string worldName)
        {
            try
            {
                string path = GetPath(worldName);
                if (!File.Exists(path))
                {
                    return new FloodState();
                }

                string raw = File.ReadAllText(path);
                FloodState state;
                if (FloodState.TryDeserialize(raw, out state))
                {
                    return state;
                }

                _logger.LogWarning("The Floods: state file could not be read, beginning a fresh inactive state.");
            }
            catch (Exception ex)
            {
                _logger.LogError("The Floods: could not load state: " + ex.Message);
            }

            return new FloodState();
        }

        internal void Save(string worldName, FloodState state)
        {
            try
            {
                Directory.CreateDirectory(_folder);
                string path = GetPath(worldName);
                string temp = path + ".tmp";
                File.WriteAllText(temp, state.Serialize());
                File.Copy(temp, path, true);
                File.Delete(temp);
            }
            catch (Exception ex)
            {
                _logger.LogError("The Floods: could not save state: " + ex.Message);
            }
        }

        private string GetPath(string worldName)
        {
            string safe = string.IsNullOrWhiteSpace(worldName) ? "UnknownWorld" : worldName;
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                safe = safe.Replace(c, '_');
            }

            return Path.Combine(_folder, safe + ".floods-state.txt");
        }
    }

    internal sealed class FloodDirector
    {
        internal const string RpcState = "TheFloods_State_01";
        internal const string RpcMessage = "TheFloods_Message_01";
        internal const string RpcStrike = "TheFloods_Strike_01";

        private readonly BepInEx.Logging.ManualLogSource _logger;
        private readonly FloodConfig _config;
        private readonly FloodWaterAdapter _water;
        private readonly FloodEnvironmentAdapter _environment;
        private readonly FloodVisuals _visuals;
        private readonly FloodStateStore _store;
        private readonly FloodLightningSystem _lightning;
        private readonly WildfireSystem _wildfires;

        private FloodState _state = new FloodState();
        private bool _loaded;
        private string _worldName;
        private float _lastSyncAt;
        private float _lastVerboseAt;
        private bool _autoTestStarted;
        private MethodInfo _findBiomeMethod;
        private MethodInfo _currentEnvironmentMethod;
        private FieldInfo _environmentNameField;
        private bool _environmentReflectionSearched;
        private MethodInfo _getSEManMethod;
        private MethodInfo _haveStatusEffectIntMethod;
        private MethodInfo _haveStatusEffectStringMethod;
        private MethodInfo _envManIsWetMethod;
        private bool _haveStatusEffectSearched;
        private bool _playerWetReflectionSearched;
        private bool _envWetReflectionSearched;
        private readonly Dictionary<string, bool> _rainEnvironmentCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        internal FloodDirector(
            BepInEx.Logging.ManualLogSource logger,
            FloodConfig config,
            FloodWaterAdapter water,
            FloodEnvironmentAdapter environment,
            FloodVisuals visuals)
        {
            _logger = logger;
            _config = config;
            _water = water;
            _environment = environment;
            _visuals = visuals;
            _store = new FloodStateStore(logger);
            _lightning = new FloodLightningSystem(logger, config, visuals);
            _wildfires = new WildfireSystem(logger, config);
        }

        internal FloodState CurrentState
        {
            get { return _state; }
        }

        internal bool IsWorldReady
        {
            get { return _loaded; }
        }

        internal bool IsAuthoritative
        {
            get { return ZNet.instance != null && ZNet.instance.IsServer(); }
        }

        internal float CurrentStormStrength
        {
            get
            {
                double now;
                return TryGetWorldSeconds(out now) ? GetStormStrength(now) : 0f;
            }
        }

        internal float CurrentWildfireIntensity
        {
            get
            {
                double now;
                return TryGetWorldSeconds(out now) ? GetWildfireIntensity(now) : 0f;
            }
        }

        internal void Tick()
        {
            if (!_config.Enabled.Value || ZNet.instance == null)
            {
                _water.RestoreOriginalWaterLevel();
                _environment.ReleaseForcedEnvironment();
                return;
            }

            double now;
            if (!TryGetWorldSeconds(out now))
            {
                return;
            }

            EnsureLoaded(now);
            ApplyLocalEffects(now);

            if (!IsAuthoritative)
            {
                return;
            }

            RunAuthoritativeState(now);

            if (Time.unscaledTime - _lastSyncAt > 4f)
            {
                BroadcastState();
            }

            if (_config.DebugLogging.Value && Time.unscaledTime - _lastVerboseAt > 30f)
            {
                _lastVerboseAt = Time.unscaledTime;
                _logger.LogInfo("The Floods: " + DescribeState(now));
            }
        }

        internal void ReceiveState(string payload)
        {
            FloodState incoming;
            if (!FloodState.TryDeserialize(payload, out incoming))
            {
                _logger.LogWarning("The Floods: ignored malformed state sync.");
                return;
            }

            if (IsAuthoritative)
            {
                return;
            }

            _state = incoming;
            _loaded = true;
        }

        internal void ReceiveStrike(string payload)
        {
            LightningStrikePayload strike;
            if (!LightningStrikePayload.TryDeserialize(payload, out strike))
            {
                _logger.LogWarning("The Floods: ignored malformed lightning strike sync.");
                return;
            }

            _lightning.RenderStrike(strike);
            ApplyLocalLightningDamage(strike);
        }

        internal void Dispose()
        {
            _lightning.Dispose();
            _wildfires.Dispose();
        }

        internal string HandleCommand(string[] args)
        {
            double now;
            if (!TryGetWorldSeconds(out now))
            {
                return "The Floods: world clock is not ready yet.";
            }

            EnsureLoaded(now);

            if (args == null || args.Length == 0 || args[0].Equals("help", StringComparison.OrdinalIgnoreCase))
            {
                return "The Floods: floods status | floods schedule | floods event stormtide|flashsurge|greatflood|drought|wildfire [omen|rise|spread|peak|recede|burnout] | floods strike | floods strikeme | floods dryness <0..1> | floods ignite [x z] | floods fireout | floods start omen|rise|peak|recede | floods set <metres> | floods fastforward | floods stop | floods probe. V0.9 uses saved climate schedules.";
            }

            string command = args[0].ToLowerInvariant();
            switch (command)
            {
                case "status":
                    return DescribeState(now);

                case "schedule":
                    return DescribeSchedule(now);

                case "event":
                    if (args.Length < 2)
                    {
                        return "Usage: floods event stormtide|flashsurge|greatflood|drought|wildfire [omen|rise|spread|peak|recede|burnout]";
                    }

                    FloodEventType requestedType;
                    if (!TryParseEventType(args[1], out requestedType) || requestedType == FloodEventType.Custom)
                    {
                        return "The Floods: choose stormtide, flashsurge, greatflood, drought, or wildfire.";
                    }

                    FloodEventProfile profile = _config.GetProfile(requestedType);
                    if (profile == null)
                    {
                        return "The Floods: profile was not found.";
                    }

                    FloodPhase requestedPhase = args.Length > 2 ? ParsePhase(args[2]) : FloodPhase.Omen;
                    if (requestedType == FloodEventType.Wildfire)
                    {
                        Vector3 origin = FindIgnitionPointNearPlayer(now, true);
                        BeginWildfireAt(origin, requestedPhase, now, true, "Smoke rises beyond the trees. Fire and flood are now both in the weather.");
                        return "The Floods: Wildfire started in " + requestedPhase + " at " + FormatVector(origin) + ".";
                    }

                    float rolledPeak = StartManualEvent(requestedType, requestedPhase, now);
                    string heightText = requestedType == FloodEventType.Drought
                        ? "Rolled drawdown: -" + Mathf.Abs(rolledPeak).ToString("0.0", CultureInfo.InvariantCulture)
                        : "Rolled height: +" + rolledPeak.ToString("0.0", CultureInfo.InvariantCulture);
                    string rangeText = requestedType == FloodEventType.Drought
                        ? "drawdown range " + profile.GetMinimumHeight().ToString("0.0", CultureInfo.InvariantCulture)
                            + "–" + profile.GetMaximumHeight().ToString("0.0", CultureInfo.InvariantCulture) + "m"
                        : "range " + profile.GetMinimumHeight().ToString("0.0", CultureInfo.InvariantCulture)
                            + "–" + profile.GetMaximumHeight().ToString("0.0", CultureInfo.InvariantCulture) + "m";
                    return "The Floods: " + profile.DisplayName + " started in " + requestedPhase
                        + ". " + heightText + " (" + rangeText + ").";

                case "start":
                    FloodPhase phase = args.Length > 1 ? ParsePhase(args[1]) : FloodPhase.Omen;
                    StartManualEvent(FloodEventType.Custom, phase, now, _config.MaximumSurgeMeters.Value);
                    return "The Floods: custom event started in " + phase + ".";

                case "set":
                    if (args.Length < 2)
                    {
                        return "Usage: floods set <metres>";
                    }

                    float meters;
                    if (!float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out meters))
                    {
                        return "The Floods: use a number such as 2.5";
                    }

                    float debugLimit = Mathf.Max(1f, _config.DebugMaximumSurgeMeters.Value);
                    meters = Mathf.Clamp(meters, -debugLimit, debugLimit);
                    StartManualEvent(FloodEventType.Custom, FloodPhase.Peak, now, meters);
                    return "The Floods: forced custom water offset set to " + FormatSignedMeters(meters) + ".";

                case "fastforward":
                    if (_state.Phase == FloodPhase.Dormant)
                    {
                        return "The Floods: no active event to advance.";
                    }

                    AdvancePhase(now);
                    return "The Floods: advanced to " + _state.Phase + ".";

                case "stop":
                    StopEvent(now, true);
                    return "The Floods: stopped and runtime water restored.";

                case "probe":
                    LogWaterProbe();
                    return "The Floods: water/environment probe written to BepInEx LogOutput.log.";

                case "strike":
                    return TriggerManualStrike(now, false);

                case "strikeme":
                    return TriggerManualStrike(now, true);

                case "dryness":
                    if (args.Length < 2)
                    {
                        return "Usage: floods dryness <0..1>";
                    }

                    float dryness;
                    if (!float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out dryness))
                    {
                        return "The Floods: use a dryness value such as 0.75";
                    }

                    _state.DrynessIndex = Mathf.Clamp01(dryness);
                    _state.LastDrynessUpdateWorldSeconds = now;
                    SaveAndBroadcast();
                    return "The Floods: dryness set to " + _state.DrynessIndex.ToString("0.00", CultureInfo.InvariantCulture) + ".";

                case "ignite":
                    return TriggerManualIgnition(args, now);

                case "fireout":
                    ExtinguishWildfire(now, true);
                    return "The Floods: wildfire nodes extinguished.";

                default:
                    return "The Floods: unknown command. Type floods help";
            }
        }

        private void EnsureLoaded(double now)
        {
            if (_loaded)
            {
                return;
            }

            _worldName = ZNet.instance.GetWorldName();
            if (string.IsNullOrWhiteSpace(_worldName))
            {
                return;
            }

            if (IsAuthoritative)
            {
                _state = _store.Load(_worldName);
                _logger.LogInfo("The Floods: loaded state for world '" + _worldName + "'. " + DescribeState(now));
            }

            _loaded = true;
        }

        private void RunAuthoritativeState(double now)
        {
            if (!_autoTestStarted && _config.StartTestEventOnWorldLoad.Value)
            {
                _autoTestStarted = true;
                StartManualEvent(FloodEventType.Custom, ParsePhase(_config.TestStartPhase.Value), now, _config.MaximumSurgeMeters.Value);
                return;
            }

            UpdateDryness(now);

            if (_state.Phase != FloodPhase.Dormant && now >= _state.PhaseEndsWorldSeconds)
            {
                AdvancePhase(now);
            }

            RunLightningScheduler(now, GetStormStrength(now));

            if (_state.Phase == FloodPhase.Dormant)
            {
                TryRunScheduledNaturalEvent(now);
            }
        }

        // V0.8 replaces the old once-per-dawn chance roll with a saved water-cycle
        // schedule. Every delay is rolled once, saved, and remains stable across
        // reloads. To players, events can begin at any hour; internally there is
        // always a clear quiet window after the previous event.
        private void TryRunScheduledNaturalEvent(double now)
        {
            EnsureWaterCycleSchedule(now);

            long day = GetWorldDay(now);
            if (day < _config.MinimumWorldDay.Value || now < _state.GlobalRecoveryEndsWorldSeconds)
            {
                return;
            }

            if (CanStartGreatFlood(now))
            {
                BeginPhase(FloodEventType.GreatFlood, FloodPhase.Omen, now, false, 0f, true);
                return;
            }

            if (now < _state.NextOrdinaryEventWorldSeconds)
            {
                TryRunWildfireSchedule(now);
                return;
            }

            FloodEventType next = SelectOrdinaryEventType();
            if (next == FloodEventType.None)
            {
                // All ordinary profiles were disabled. Recheck later rather than
                // hammering the host every frame.
                _state.NextOrdinaryEventWorldSeconds = now + (30d * 60d);
                SaveAndBroadcast();
                return;
            }

            BeginPhase(next, FloodPhase.Omen, now, false, 0f, true);
        }

        private void EnsureWaterCycleSchedule(double now)
        {
            if (_state.SchedulerInitialized)
            {
                return;
            }

            long day = GetWorldDay(now);
            double secondsPerDay = Math.Max(60d, _config.GameDaySeconds.Value);
            double earliestOrdinary = now + Math.Max(0L, (long)_config.MinimumWorldDay.Value - day) * secondsPerDay;
            float firstDelayHours = RollSchedulerHours(
                _config.FirstOrdinaryEventMinimumHours.Value,
                _config.FirstOrdinaryEventMaximumHours.Value,
                "FirstOrdinary");
            _state.GlobalRecoveryEndsWorldSeconds = earliestOrdinary;
            _state.NextOrdinaryEventWorldSeconds = earliestOrdinary + HoursToSeconds(firstDelayHours);

            ScheduleFirstGreatFlood(now);
            ScheduleNextWildfireCheck(now);

            _state.SchedulerInitialized = true;
            SaveAndBroadcast();

            if (_config.DebugLogging.Value)
            {
                _logger.LogInfo("The Floods V0.8 scheduler initialized. " + DescribeSchedule(now));
            }
        }

        private void ScheduleFirstGreatFlood(double now)
        {
            double currentAgeHours = GetWorldAgeHours(now);
            float targetAgeHours = RollSchedulerHours(
                _config.GreatFloodFirstTargetMinimumWorldHours.Value,
                _config.GreatFloodFirstTargetMaximumWorldHours.Value,
                "FirstGreatTarget");
            float eligibleAgeHours = Mathf.Max(0f, _config.GreatFloodFirstEligibleWorldHours.Value);
            float forceAgeHours = Mathf.Max(
                Mathf.Max(eligibleAgeHours, targetAgeHours),
                _config.GreatFloodFirstForceByWorldHours.Value);

            // Existing worlds may already be older than the intended target.
            // Give them a short random lead-in instead of beginning a major
            // flood the instant V0.8 is installed.
            double targetDelayHours = targetAgeHours - currentAgeHours;
            if (targetDelayHours <= 0d)
            {
                targetDelayHours = RollSchedulerHours(0.75f, 2.5f, "MatureWorldFirstGreat");
            }

            double forceDelayHours = forceAgeHours - currentAgeHours;
            if (forceDelayHours <= 0d)
            {
                forceDelayHours = 3d;
            }

            _state.NextGreatFloodWorldSeconds = now + HoursToSeconds((float)Math.Max(targetDelayHours, eligibleAgeHours - currentAgeHours));
            _state.GreatFloodForceWorldSeconds = now + HoursToSeconds((float)Math.Max(forceDelayHours, 1d));
        }

        private void ScheduleNextGreatFlood(double now)
        {
            float delay = RollSchedulerHours(
                _config.GreatFloodRepeatMinimumHours.Value,
                _config.GreatFloodRepeatMaximumHours.Value,
                "RepeatGreat");
            float forceDelay = Mathf.Max(delay, _config.GreatFloodRepeatForceByHours.Value);

            _state.NextGreatFloodWorldSeconds = now + HoursToSeconds(delay);
            _state.GreatFloodForceWorldSeconds = now + HoursToSeconds(forceDelay);
        }

        private bool CanStartGreatFlood(double now)
        {
            FloodEventProfile profile = _config.GreatFlood;
            if (profile == null || !profile.Enabled.Value)
            {
                return false;
            }

            if (GetWorldAgeHours(now) < Mathf.Max(0f, _config.GreatFloodFirstEligibleWorldHours.Value))
            {
                return false;
            }

            return now >= _state.NextGreatFloodWorldSeconds || now >= _state.GreatFloodForceWorldSeconds;
        }

        private void ScheduleAfterNaturalEvent(FloodEventType completedType, double now)
        {
            float quietHours;
            if (completedType == FloodEventType.FlashSurge)
            {
                quietHours = RollSchedulerHours(
                    _config.FlashSurgeRecoveryMinimumHours.Value,
                    _config.FlashSurgeRecoveryMaximumHours.Value,
                    "AfterFlash");
            }
            else if (completedType == FloodEventType.GreatFlood)
            {
                quietHours = RollSchedulerHours(
                    _config.GreatFloodRecoveryMinimumHours.Value,
                    _config.GreatFloodRecoveryMaximumHours.Value,
                    "AfterGreat");
                ScheduleNextGreatFlood(now);
            }
            else
            {
                quietHours = RollSchedulerHours(
                    _config.OrdinaryRecoveryMinimumHours.Value,
                    _config.OrdinaryRecoveryMaximumHours.Value,
                    "AfterOrdinary");
            }

            _state.GlobalRecoveryEndsWorldSeconds = now + HoursToSeconds(quietHours);
            _state.NextOrdinaryEventWorldSeconds = _state.GlobalRecoveryEndsWorldSeconds;
            ScheduleNextWildfireCheck(now);
        }

        private void UpdateDryness(double now)
        {
            if (_state.LastDrynessUpdateWorldSeconds <= 0d || now < _state.LastDrynessUpdateWorldSeconds)
            {
                _state.LastDrynessUpdateWorldSeconds = now;
                return;
            }

            double elapsed = Math.Min(30d, Math.Max(0d, now - _state.LastDrynessUpdateWorldSeconds));
            _state.LastDrynessUpdateWorldSeconds = now;
            if (elapsed <= 0.001d)
            {
                return;
            }

            float before = _state.DrynessIndex;
            float storm = GetStormStrength(now);
            if (IsGroundWetForFire(now, storm))
            {
                float resetSeconds = Mathf.Max(30f, _config.DrynessRainResetSeconds.Value);
                _state.DrynessIndex = Mathf.Clamp01(_state.DrynessIndex - (float)(elapsed / resetSeconds));
            }
            else
            {
                float multiplier = _state.EventType == FloodEventType.Drought && _state.Phase != FloodPhase.Dormant
                    ? Mathf.Max(1f, _config.DrynessRiseDroughtMultiplier.Value)
                    : 1f;
                _state.DrynessIndex = Mathf.Clamp01(_state.DrynessIndex + (float)(elapsed / 3600d) * Mathf.Max(0f, _config.DrynessRisePerHour.Value) * multiplier);
            }

            if (Mathf.Abs(before - _state.DrynessIndex) > 0.01f && Time.unscaledTime - _lastSyncAt > 8f)
            {
                SaveAndBroadcast();
            }
        }

        private void ScheduleNextWildfireCheck(double now)
        {
            float delay = RollSchedulerHours(
                _config.SpontaneousWindowMinimumHours.Value,
                _config.SpontaneousWindowMaximumHours.Value,
                "WildfireWindow");
            float maxWindow = Mathf.Max(delay, Mathf.Max(_config.SpontaneousWindowMinimumHours.Value, _config.SpontaneousWindowMaximumHours.Value));
            _state.NextWildfireWorldSeconds = now + HoursToSeconds(delay);
            _state.WildfireForceWorldSeconds = now + HoursToSeconds(Mathf.Max(maxWindow * 4f, delay));
        }

        private void TryRunWildfireSchedule(double now)
        {
            if (_state.EventType != FloodEventType.None || _state.Phase != FloodPhase.Dormant)
            {
                return;
            }

            FloodEventProfile profile = _config.Wildfire;
            if (profile == null || !profile.Enabled.Value)
            {
                return;
            }

            if (_state.NextWildfireWorldSeconds <= 0d)
            {
                ScheduleNextWildfireCheck(now);
                SaveAndBroadcast();
                return;
            }

            if (_state.DrynessIndex < Mathf.Clamp01(_config.SpontaneousDrynessThreshold.Value) && now < _state.WildfireForceWorldSeconds)
            {
                return;
            }

            if (now < _state.NextWildfireWorldSeconds && now < _state.WildfireForceWorldSeconds)
            {
                return;
            }

            Vector3 point = FindIgnitionPointNearPlayer(now, false);
            if (point == Vector3.zero)
            {
                ScheduleNextWildfireCheck(now);
                SaveAndBroadcast();
                return;
            }

            float flammability = GetBiomeFlammability(point);
            float chance = Mathf.Clamp01(_config.SpontaneousPerWindowChance.Value * Mathf.Clamp01(_state.DrynessIndex) * flammability);
            bool forced = now >= _state.WildfireForceWorldSeconds && _state.DrynessIndex >= Mathf.Clamp01(_config.SpontaneousDrynessThreshold.Value);
            _state.SchedulerCycle++;
            float roll = DeterministicRoll(_worldName + "|Floods|WildfireSpontaneous|" + _state.SchedulerCycle.ToString(CultureInfo.InvariantCulture));
            if (forced || roll < chance)
            {
                BeginWildfireAt(point, FloodPhase.Omen, now, false, "Smoke rises beyond the trees. Drought has made the land ready to burn.");
                return;
            }

            ScheduleNextWildfireCheck(now);
            SaveAndBroadcast();
        }

        private void RunLightningScheduler(double now, float stormStrength)
        {
            if (!_config.EnableLightningStrikes.Value || stormStrength < Mathf.Clamp01(_config.PlayerStrikeMinStrength.Value))
            {
                return;
            }

            if (_state.Phase == FloodPhase.Omen || _state.Phase == FloodPhase.Dormant)
            {
                return;
            }

            if (_state.NextStrikeWorldSeconds <= 0d || _state.NextStrikeWorldSeconds < now - 300d)
            {
                ScheduleNextStrike(now, stormStrength);
                SaveAndBroadcast();
                return;
            }

            if (now < _state.NextStrikeWorldSeconds)
            {
                return;
            }

            Player target = PickLightningTarget();
            if (target == null)
            {
                ScheduleNextStrike(now + 10d, stormStrength);
                SaveAndBroadcast();
                return;
            }

            _state.SchedulerCycle++;
            float directRoll = DeterministicRoll(_worldName + "|Floods|LightningDirect|" + _state.SchedulerCycle.ToString(CultureInfo.InvariantCulture));
            bool direct = directRoll < Mathf.Clamp01(_config.DirectHitChance.Value) && IsPlayerExposed(target);

            Vector3 strikePoint = direct ? target.transform.position : PickNearMissPoint(target);
            float damage = direct ? RollLightningDamage(target, stormStrength) : 0f;
            long targetId = direct ? GetPlayerId(target) : 0L;

            _state.SchedulerCycle++;
            long seed = StableHash(_worldName + "|Floods|Strike|" + _state.SchedulerCycle.ToString(CultureInfo.InvariantCulture) + "|" + now.ToString("R", CultureInfo.InvariantCulture));
            int ignite = ShouldLightningIgnite(strikePoint, stormStrength, seed) ? 1 : 0;
            if (ignite == 1 && IsAuthoritative)
            {
                BeginWildfireAt(strikePoint, FloodPhase.Omen, now, false, "Dry lightning splits the sky. Fire takes hold where the land stayed thirsty.");
            }

            LightningStrikePayload payload = new LightningStrikePayload
            {
                Position = strikePoint,
                Damage = damage,
                IsDirectHit = direct,
                TargetPlayerId = targetId,
                Ignite = ignite,
                Seed = seed
            };

            BroadcastStrike(payload);
            ScheduleNextStrike(now, stormStrength);
            SaveAndBroadcast();
        }

        private void ScheduleNextStrike(double now, float stormStrength)
        {
            float minimum = Mathf.Max(3f, Mathf.Min(_config.StrikeGapMinSeconds.Value, _config.StrikeGapMaxSeconds.Value));
            float maximum = Mathf.Max(minimum, Mathf.Max(_config.StrikeGapMinSeconds.Value, _config.StrikeGapMaxSeconds.Value));
            _state.SchedulerCycle++;
            float roll = DeterministicRoll(_worldName + "|Floods|StrikeGap|" + _state.SchedulerCycle.ToString(CultureInfo.InvariantCulture));
            float baseGap = Mathf.Lerp(maximum, minimum, Mathf.Clamp01(stormStrength));
            float jitter = Mathf.Lerp(0.75f, 1.25f, roll);
            _state.NextStrikeWorldSeconds = now + Math.Max(2d, baseGap * jitter);
        }

        private Player PickLightningTarget()
        {
            List<Player> players = GetLoadedPlayers();
            if (players.Count == 0)
            {
                return null;
            }

            List<Player> exposed = new List<Player>();
            for (int i = 0; i < players.Count; i++)
            {
                if (players[i] != null && IsPlayerExposed(players[i]))
                {
                    exposed.Add(players[i]);
                }
            }

            if (exposed.Count == 0)
            {
                return null;
            }

            _state.SchedulerCycle++;
            int index = Mathf.FloorToInt(DeterministicRoll(_worldName + "|Floods|StrikeTarget|" + _state.SchedulerCycle.ToString(CultureInfo.InvariantCulture)) * exposed.Count);
            return exposed[Mathf.Clamp(index, 0, exposed.Count - 1)];
        }

        private Vector3 PickNearMissPoint(Player target)
        {
            Vector3 center = target == null ? Vector3.zero : target.transform.position;
            float minimum = Mathf.Max(8f, _config.IgnitionMinRadius.Value * 0.25f);
            float maximum = Mathf.Max(minimum + 1f, Mathf.Min(35f, _config.IgnitionMaxRadius.Value * 0.45f));
            float angle = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
            float radius = UnityEngine.Random.Range(minimum, maximum);
            Vector3 candidate = center + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
            Vector3 grounded;
            return TryFindGround(candidate, out grounded) ? grounded : candidate;
        }

        private bool ShouldLightningIgnite(Vector3 point, float stormStrength, long seed)
        {
            if (!IsIgnitablePoint(point, true, stormStrength))
            {
                return false;
            }

            float flammability = GetBiomeFlammability(point);
            float chance = Mathf.Clamp01(_config.LightningIgnitionChance.Value * flammability * Mathf.Clamp01(_state.DrynessIndex));
            float roll = DeterministicRoll(_worldName + "|Floods|LightningIgnition|" + seed.ToString(CultureInfo.InvariantCulture));
            return roll < chance;
        }

        private void BroadcastStrike(LightningStrikePayload payload)
        {
            if (ZRoutedRpc.instance == null || payload == null)
            {
                return;
            }

            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, RpcStrike, new object[] { payload.Serialize() });
        }

        private void ApplyLocalLightningDamage(LightningStrikePayload strike)
        {
            if (strike == null || !strike.IsDirectHit || strike.Damage <= 0.001f || Player.m_localPlayer == null)
            {
                return;
            }

            Player local = Player.m_localPlayer;
            long localId = GetPlayerId(local);
            bool matches = strike.TargetPlayerId != 0L && localId == strike.TargetPlayerId;
            if (!matches && strike.TargetPlayerId == 0L)
            {
                matches = (local.transform.position - strike.Position).sqrMagnitude <= 9f;
            }

            if (!matches)
            {
                return;
            }

            float damage = strike.Damage;
            if (_config.NonLethalLightning.Value)
            {
                float health = GetPlayerHealth(local);
                if (health > 1f)
                {
                    damage = Mathf.Min(damage, health - 1f);
                }
            }

            HitData hit = new HitData();
            float healthBefore = GetPlayerHealth(local);
            hit.m_damage.m_lightning = Mathf.Max(0f, damage);
            hit.m_point = strike.Position;
            hit.m_dir = Vector3.down;
            hit.m_pushForce = Mathf.Max(0f, _config.LightningPushForce.Value);
            hit.m_hitType = HitData.HitType.Undefined;
            local.Damage(hit);

            if (MessageHud.instance != null)
            {
                MessageHud.instance.ShowMessage(MessageHud.MessageType.Center, damage >= healthBefore ? "You are struck down by the storm." : "Lightning tears through you!");
            }
        }

        private float RollLightningDamage(Player target, float stormStrength)
        {
            float damage = Mathf.Lerp(Mathf.Max(0f, _config.MinLightningDamage.Value), Mathf.Max(_config.MinLightningDamage.Value, _config.MaxLightningDamage.Value), Mathf.Clamp01(stormStrength));
            if (target != null && IsPlayerWet(target))
            {
                damage *= Mathf.Max(0.1f, _config.WetDamageMultiplier.Value);
            }

            return damage;
        }

        private string TriggerManualStrike(double now, bool direct)
        {
            Player player = Player.m_localPlayer;
            if (player == null)
            {
                return "The Floods: no local player found for strike test.";
            }

            Vector3 point = direct ? player.transform.position : PickNearMissPoint(player);
            float storm = Mathf.Max(GetStormStrength(now), 0.9f);
            float damage = direct ? RollLightningDamage(player, storm) : 0f;
            LightningStrikePayload payload = new LightningStrikePayload
            {
                Position = point,
                Damage = damage,
                IsDirectHit = direct,
                TargetPlayerId = direct ? GetPlayerId(player) : 0L,
                Ignite = 0,
                Seed = StableHash(_worldName + "|Floods|ManualStrike|" + now.ToString("R", CultureInfo.InvariantCulture))
            };

            BroadcastStrike(payload);
            _state.NextStrikeWorldSeconds = now + Mathf.Max(2f, _config.StrikeGraceSeconds.Value);
            SaveAndBroadcast();
            return direct
                ? "The Floods: direct lightning strike requested at your position for " + damage.ToString("0", CultureInfo.InvariantCulture) + " lightning damage."
                : "The Floods: near-miss lightning strike requested at " + FormatVector(point) + ".";
        }

        private string TriggerManualIgnition(string[] args, double now)
        {
            Vector3 origin;
            if (args.Length >= 3)
            {
                float x;
                float z;
                if (!float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                    || !float.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out z))
                {
                    return "Usage: floods ignite [x z]";
                }

                Vector3 candidate = new Vector3(x, Player.m_localPlayer == null ? 35f : Player.m_localPlayer.transform.position.y, z);
                if (!TryFindGround(candidate, out origin))
                {
                    origin = candidate;
                }
            }
            else
            {
                origin = FindIgnitionPointNearPlayer(now, true);
            }

            _wildfires.Ignite(origin, now, StableHash(_worldName + "|Floods|ManualIgnite|" + now.ToString("R", CultureInfo.InvariantCulture)));
            return "The Floods: test fire node ignited at " + FormatVector(origin) + ".";
        }

        private void BeginWildfireAt(Vector3 origin, FloodPhase phase, double now, bool manual, string reason)
        {
            if (origin == Vector3.zero)
            {
                origin = FindIgnitionPointNearPlayer(now, true);
            }

            _state.WildfireOriginX = origin.x;
            _state.WildfireOriginY = origin.y;
            _state.WildfireOriginZ = origin.z;
            BeginPhase(FloodEventType.Wildfire, phase, now, manual, 0f, false);
            _wildfires.Ignite(origin, now, _state.EventSeed);

            if (!string.IsNullOrEmpty(reason))
            {
                BroadcastMessage(reason);
            }
        }

        private void ExtinguishWildfire(double now, bool manual)
        {
            _wildfires.ExtinguishAll();
            if (_state.EventType == FloodEventType.Wildfire)
            {
                StopEvent(now, manual);
                return;
            }

            SaveAndBroadcast();
        }

        private Vector3 FindIgnitionPointNearPlayer(double now, bool allowFallback)
        {
            List<Player> players = GetLoadedPlayers();
            if (players.Count == 0 && Player.m_localPlayer != null)
            {
                players.Add(Player.m_localPlayer);
            }

            if (players.Count == 0)
            {
                return Vector3.zero;
            }

            float minimum = Mathf.Max(5f, Mathf.Min(_config.IgnitionMinRadius.Value, _config.IgnitionMaxRadius.Value));
            float maximum = Mathf.Max(minimum + 1f, Mathf.Max(_config.IgnitionMinRadius.Value, _config.IgnitionMaxRadius.Value));
            for (int attempt = 0; attempt < 16; attempt++)
            {
                Player player = players[UnityEngine.Random.Range(0, players.Count)];
                if (player == null)
                {
                    continue;
                }

                float angle = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
                float radius = UnityEngine.Random.Range(minimum, maximum);
                Vector3 candidate = player.transform.position + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
                Vector3 grounded;
                if (!TryFindGround(candidate, out grounded))
                {
                    continue;
                }

                if (allowFallback || IsIgnitablePoint(grounded, true, GetStormStrength(now)))
                {
                    return grounded;
                }
            }

            if (!allowFallback)
            {
                return Vector3.zero;
            }

            Player fallback = players[0];
            Vector3 fallbackPoint = fallback.transform.position + fallback.transform.forward * minimum;
            Vector3 groundedFallback;
            return TryFindGround(fallbackPoint, out groundedFallback) ? groundedFallback : fallbackPoint;
        }

        private bool TryFindGround(Vector3 candidate, out Vector3 grounded)
        {
            grounded = candidate;
            bool foundTerrain = false;
            try
            {
                Type zs = AccessTools.TypeByName("ZoneSystem");
                if (zs != null)
                {
                    object inst = AccessTools.Property(zs, "instance")?.GetValue(null) ?? AccessTools.Field(zs, "m_instance")?.GetValue(null);
                    if (inst != null)
                    {
                        MethodInfo getGroundHeight = AccessTools.Method(zs, "GetGroundHeight", new[] { typeof(Vector3) });
                        if (getGroundHeight != null)
                        {
                            object res = getGroundHeight.Invoke(inst, new object[] { candidate });
                            if (res is float h && h > -1000f)
                            {
                                grounded = new Vector3(candidate.x, h, candidate.z);
                                foundTerrain = true;
                            }
                        }
                    }
                }
            }
            catch { }

            try
            {
                Vector3 start = new Vector3(candidate.x, candidate.y + 140f, candidate.z);
                if (foundTerrain) return true;

                // FIX: Picking the highest point prevents Ashlands/Bog Witch deep seabed issues
                RaycastHit[] hits = Physics.RaycastAll(start, Vector3.down, 300f, ~0, QueryTriggerInteraction.Ignore);
                if (hits.Length > 0)
                {
                    RaycastHit highest = hits[0];
                    for (int i = 1; i < hits.Length; i++)
                    {
                        if (hits[i].point.y > highest.point.y)
                        {
                            highest = hits[i];
                        }
                    }
                    grounded = highest.point;
                    return true;
                }
            }
            catch
            {
                // The caller can use the ungrounded candidate.
            }

            return foundTerrain;
        }

        private bool IsIgnitableForSpread(Vector3 point)
        {
            return IsIgnitablePoint(point, true, GetStormStrength(TryGetWorldSeconds(out double now) ? now : 0d));
        }

        private bool IsIgnitablePoint(Vector3 point, bool requireDryness, float stormStrength)
        {
            if (requireDryness && _state.DrynessIndex < Mathf.Clamp01(_config.SpontaneousDrynessThreshold.Value))
            {
                return false;
            }

            if (IsGroundWetForFire(TryGetWorldSeconds(out double now) ? now : 0d, stormStrength))
            {
                return false;
            }

            if (_water.IsPointUnderKnownWater(point))
            {
                return false;
            }

            return GetBiomeFlammability(point) > 0.01f;
        }

        private bool IsGroundWetForFire(double now, float stormStrength)
        {
            if (IsWorldWetFromEnvMan())
            {
                return true;
            }

            if (_state.EventType == FloodEventType.StormTide
                || _state.EventType == FloodEventType.FlashSurge
                || _state.EventType == FloodEventType.GreatFlood)
            {
                if (_state.Phase != FloodPhase.Omen && stormStrength >= Mathf.Clamp01(_config.RainWetStormStrength.Value))
                {
                    return true;
                }
            }

            string environmentName = GetCurrentEnvironmentName();
            if (IsRainEnvironmentName(environmentName))
            {
                return stormStrength <= 0.001f || stormStrength >= Mathf.Clamp01(_config.RainWetStormStrength.Value) || _state.EventType == FloodEventType.Wildfire;
            }

            return false;
        }

        private float GetBiomeFlammability(Vector3 point)
        {
            string biome = GetBiomeName(point);
            string normalized = (biome ?? string.Empty).Replace(" ", string.Empty).Replace("_", string.Empty).ToLowerInvariant();
            if (normalized.Contains("meadows"))
            {
                return Mathf.Max(0f, _config.MeadowsFlammability.Value);
            }

            if (normalized.Contains("blackforest"))
            {
                return Mathf.Max(0f, _config.BlackForestFlammability.Value);
            }

            if (normalized.Contains("plains"))
            {
                return Mathf.Max(0f, _config.PlainsFlammability.Value);
            }

            if (normalized.Contains("swamp"))
            {
                return Mathf.Max(0f, _config.SwampFlammability.Value);
            }

            if (normalized.Contains("mountain"))
            {
                return Mathf.Max(0f, _config.MountainFlammability.Value);
            }

            if (normalized.Contains("mistlands"))
            {
                return Mathf.Max(0f, _config.MistlandsFlammability.Value);
            }

            if (normalized.Contains("ashlands"))
            {
                return Mathf.Max(0f, _config.AshlandsFlammability.Value);
            }

            if (normalized.Contains("deepnorth"))
            {
                return Mathf.Max(0f, _config.DeepNorthFlammability.Value);
            }

            if (normalized.Contains("ocean"))
            {
                return 0f;
            }

            return Mathf.Max(0f, _config.OtherBiomeFlammability.Value);
        }

        private string GetBiomeName(Vector3 point)
        {
            try
            {
                if (_findBiomeMethod == null)
                {
                    _findBiomeMethod = AccessTools.Method(typeof(Heightmap), "FindBiome", new[] { typeof(Vector3) });
                }

                if (_findBiomeMethod != null)
                {
                    object result = _findBiomeMethod.Invoke(null, new object[] { point });
                    return result == null ? string.Empty : result.ToString();
                }
            }
            catch
            {
                // Unknown biome falls back to the configurable Other value.
            }

            return string.Empty;
        }

        private string GetCurrentEnvironmentName()
        {
            if (EnvMan.instance == null)
            {
                return string.Empty;
            }

            try
            {
                if (!_environmentReflectionSearched)
                {
                    _environmentReflectionSearched = true;
                    _currentEnvironmentMethod = AccessTools.Method(typeof(EnvMan), "GetCurrentEnvironment", Type.EmptyTypes);
                }

                object env = _currentEnvironmentMethod == null ? null : _currentEnvironmentMethod.Invoke(EnvMan.instance, null);
                if (env == null)
                {
                    return string.Empty;
                }

                if (_environmentNameField == null)
                {
                    _environmentNameField = AccessTools.Field(env.GetType(), "m_name");
                }

                object name = _environmentNameField == null ? null : _environmentNameField.GetValue(env);
                return name as string ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private bool IsRainEnvironmentName(string environmentName)
        {
            if (string.IsNullOrWhiteSpace(environmentName))
            {
                return false;
            }

            bool cached;
            if (_rainEnvironmentCache.TryGetValue(environmentName, out cached))
            {
                return cached;
            }

            string[] parts = (_config.RainEnvironmentNames.Value ?? string.Empty).Split(',');
            bool found = false;
            for (int i = 0; i < parts.Length; i++)
            {
                string name = parts[i].Trim();
                if (name.Length > 0 && environmentName.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    found = true;
                    break;
                }
            }

            _rainEnvironmentCache[environmentName] = found;
            return found;
        }

        private Vector3 GetWindDirection()
        {
            try
            {
                if (EnvMan.instance != null)
                {
                    Vector3 windDir = EnvMan.instance.GetWindDir();
                    windDir.y = 0f;
                    if (windDir.sqrMagnitude > 0.0001f)
                    {
                        return windDir.normalized;
                    }
                }
            }
            catch
            {
                // Fallback below.
            }

            return Vector3.forward;
        }

        private List<Player> GetLoadedPlayers()
        {
            List<Player> players = new List<Player>();
            try
            {
                MethodInfo getAll = AccessTools.Method(typeof(Player), "GetAllPlayers", Type.EmptyTypes);
                object result = getAll == null ? null : getAll.Invoke(null, null);
                IEnumerable enumerable = result as IEnumerable;
                if (enumerable != null)
                {
                    foreach (object item in enumerable)
                    {
                        Player player = item as Player;
                        if (player != null && !players.Contains(player))
                        {
                            players.Add(player);
                        }
                    }
                }
            }
            catch
            {
                // The local player fallback below keeps single-player tests working.
            }

            if (Player.m_localPlayer != null && !players.Contains(Player.m_localPlayer))
            {
                players.Add(Player.m_localPlayer);
            }

            return players;
        }

        private bool IsPlayerExposed(Player player)
        {
            if (player == null || !_config.RequireExposed.Value)
            {
                return true;
            }

            return !IsPlayerSheltered(player);
        }

        private bool IsPlayerSheltered(Player player)
        {
            if (player == null)
            {
                return false;
            }

            try
            {
                MethodInfo inShelter = AccessTools.Method(player.GetType(), "InShelter", Type.EmptyTypes);
                if (inShelter != null)
                {
                    object result = inShelter.Invoke(player, null);
                    if (result is bool)
                    {
                        return (bool)result;
                    }
                }
            }
            catch
            {
                // Missing shelter API means not sheltered.
            }

            return false;
        }

        private bool IsPlayerWet(Player player)
        {
            if (player == null)
            {
                return false;
            }

            try
            {
                if (!_playerWetReflectionSearched)
                {
                    _playerWetReflectionSearched = true;
                    _getSEManMethod = AccessTools.Method(player.GetType(), "GetSEMan", Type.EmptyTypes);
                }

                object seMan = _getSEManMethod == null ? null : _getSEManMethod.Invoke(player, null);
                if (seMan != null)
                {
                    Type seManType = seMan.GetType();
                    if (!_haveStatusEffectSearched)
                    {
                        _haveStatusEffectSearched = true;
                        _haveStatusEffectIntMethod = AccessTools.Method(seManType, "HaveStatusEffect", new[] { typeof(int) });
                        _haveStatusEffectStringMethod = AccessTools.Method(seManType, "HaveStatusEffect", new[] { typeof(string) });
                    }

                    if (_haveStatusEffectIntMethod != null)
                    {
                        object result = _haveStatusEffectIntMethod.Invoke(seMan, new object[] { GetStableStatusHash("Wet") });
                        if (result is bool && (bool)result)
                        {
                            return true;
                        }
                    }

                    if (_haveStatusEffectStringMethod != null)
                    {
                        object result = _haveStatusEffectStringMethod.Invoke(seMan, new object[] { "Wet" });
                        if (result is bool && (bool)result)
                        {
                            return true;
                        }
                    }
                }
            }
            catch
            {
                // Fall back to environment wetness below.
            }

            return IsWorldWetFromEnvMan() && !IsPlayerSheltered(player);
        }

        private bool IsWorldWetFromEnvMan()
        {
            if (EnvMan.instance == null)
            {
                return false;
            }

            try
            {
                if (!_envWetReflectionSearched)
                {
                    _envWetReflectionSearched = true;
                    _envManIsWetMethod = AccessTools.Method(typeof(EnvMan), "IsWet", Type.EmptyTypes);
                }

                if (_envManIsWetMethod == null)
                {
                    _envManIsWetMethod = AccessTools.Method(typeof(EnvMan), "IsWet", Type.EmptyTypes);
                }

                if (_envManIsWetMethod != null)
                {
                    object result = _envManIsWetMethod.Invoke(EnvMan.instance, null);
                    return result is bool && (bool)result;
                }
            }
            catch
            {
                // Environment wetness is optional.
            }

            return false;
        }

        private static int GetStableStatusHash(string text)
        {
            unchecked
            {
                int hash = 5381;
                for (int i = 0; i < text.Length; i++)
                {
                    hash = ((hash << 5) + hash) ^ text[i];
                }

                return hash;
            }
        }

        private float GetPlayerHealth(Player player)
        {
            if (player == null)
            {
                return 0f;
            }

            try
            {
                MethodInfo getHealth = AccessTools.Method(player.GetType(), "GetHealth", Type.EmptyTypes);
                if (getHealth != null)
                {
                    object result = getHealth.Invoke(player, null);
                    if (result != null)
                    {
                        return Convert.ToSingle(result, CultureInfo.InvariantCulture);
                    }
                }
            }
            catch
            {
                // Unknown health keeps nonlethal clamping conservative.
            }

            return 9999f;
        }

        private static long GetPlayerId(Player player)
        {
            if (player == null)
            {
                return 0L;
            }

            try
            {
                MethodInfo getId = AccessTools.Method(player.GetType(), "GetPlayerID", Type.EmptyTypes);
                if (getId != null)
                {
                    object result = getId.Invoke(player, null);
                    if (result != null)
                    {
                        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
                    }
                }

                FieldInfo idField = AccessTools.Field(player.GetType(), "m_playerID");
                object fieldValue = idField == null ? null : idField.GetValue(player);
                if (fieldValue != null)
                {
                    return Convert.ToInt64(fieldValue, CultureInfo.InvariantCulture);
                }
            }
            catch
            {
                // Fallback below.
            }

            return StableHash(player.name + "|" + player.GetInstanceID().ToString(CultureInfo.InvariantCulture));
        }

        private static string FormatVector(Vector3 value)
        {
            return "(" + value.x.ToString("0.0", CultureInfo.InvariantCulture)
                + "," + value.y.ToString("0.0", CultureInfo.InvariantCulture)
                + "," + value.z.ToString("0.0", CultureInfo.InvariantCulture) + ")";
        }

        private FloodEventType SelectOrdinaryEventType()
        {
            float stormWeight = _config.StormTide.Enabled.Value ? Mathf.Max(0f, _config.StormTideWeight.Value) : 0f;
            float droughtWeight = _config.Drought.Enabled.Value ? Mathf.Max(0f, _config.DroughtWeight.Value) : 0f;
            float flashWeight = _config.FlashSurge.Enabled.Value ? Mathf.Max(0f, _config.FlashSurgeWeight.Value) : 0f;
            float total = stormWeight + droughtWeight + flashWeight;
            if (total <= 0.0001f)
            {
                return FloodEventType.None;
            }

            _state.SchedulerCycle++;
            float roll = DeterministicRoll(
                _worldName + "|Floods|OrdinaryType|" +
                _state.SchedulerCycle.ToString(CultureInfo.InvariantCulture) + "|" +
                _state.EventNumber.ToString(CultureInfo.InvariantCulture)) * total;

            if (roll < stormWeight)
            {
                return FloodEventType.StormTide;
            }

            roll -= stormWeight;
            if (roll < droughtWeight)
            {
                return FloodEventType.Drought;
            }

            return FloodEventType.FlashSurge;
        }

        private float RollSchedulerHours(float first, float second, string purpose)
        {
            float minimum = Mathf.Max(0.01f, Mathf.Min(first, second));
            float maximum = Mathf.Max(minimum, Mathf.Max(first, second));
            _state.SchedulerCycle++;
            float roll = DeterministicRoll(
                _worldName + "|Floods|Scheduler|" + purpose + "|" +
                _state.SchedulerCycle.ToString(CultureInfo.InvariantCulture) + "|" +
                _state.EventNumber.ToString(CultureInfo.InvariantCulture));
            return Mathf.Lerp(minimum, maximum, roll);
        }

        private double GetWorldAgeHours(double now)
        {
            long day = GetWorldDay(now);
            return Math.Max(0d, day * Math.Max(60d, _config.GameDaySeconds.Value) / 3600d);
        }

        private static double HoursToSeconds(float hours)
        {
            return Math.Max(0.01d, hours) * 3600d;
        }

        // Named events select a random height only once at their beginning. The
        // peak is then carried unchanged through every phase and saved to disk.
        private float StartManualEvent(FloodEventType eventType, FloodPhase phase, double now)
        {
            BeginPhase(eventType, phase, now, true, 0f, true);
            return _state.PeakMeters;
        }

        private void StartManualEvent(FloodEventType eventType, FloodPhase phase, double now, float peakMeters)
        {
            BeginPhase(eventType, phase, now, true, peakMeters, false);
        }

        private void BeginPhase(FloodEventType eventType, FloodPhase phase, double now, bool manual, float requestedPeakMeters, bool rollProfileHeight)
        {
            if (phase == FloodPhase.Dormant)
            {
                StopEvent(now, manual);
                return;
            }

            bool beginsNewEvent = _state.Phase == FloodPhase.Dormant
                || phase == FloodPhase.Omen
                || _state.EventType != eventType;

            if (beginsNewEvent)
            {
                _state.EventNumber++;
                _state.EventSeed = StableHash(_worldName + "|" + eventType + "|" + _state.EventNumber + "|" + now.ToString("R", CultureInfo.InvariantCulture));
            }

            _state.EventType = eventType;
            _state.Phase = phase;
            _state.PhaseStartedWorldSeconds = now;
            _state.PhaseEndsWorldSeconds = now + GetPhaseDurationSeconds(phase);

            if (beginsNewEvent)
            {
                bool severeDrought = false;
                float selectedPeak = rollProfileHeight
                    ? RollEventPeakMeters(eventType, _state.EventSeed, out severeDrought)
                    : requestedPeakMeters;
                float debugLimit = Mathf.Max(1f, _config.DebugMaximumSurgeMeters.Value);
                _state.PeakMeters = Mathf.Clamp(selectedPeak, -debugLimit, debugLimit);
                _state.IsSevereDrought = eventType == FloodEventType.Drought && rollProfileHeight && severeDrought;
                _state.DrawdownMeters = eventType == FloodEventType.FlashSurge && _config.EnableFlashSurgeDrawdown.Value
                    ? RollFlashSurgeDrawdownMeters(_state.EventSeed)
                    : 0f;
            }
            else if (!rollProfileHeight)
            {
                float debugLimit = Mathf.Max(1f, _config.DebugMaximumSurgeMeters.Value);
                _state.PeakMeters = Mathf.Clamp(requestedPeakMeters, -debugLimit, debugLimit);
            }

            _state.ManualEvent = manual;

            AnnouncePhase(eventType, phase);
            SaveAndBroadcast();
        }

        private float RollEventPeakMeters(FloodEventType eventType, long eventSeed, out bool severeDrought)
        {
            severeDrought = false;
            FloodEventProfile profile = _config.GetProfile(eventType);
            if (profile == null)
            {
                return _config.MaximumSurgeMeters.Value;
            }

            float minimum = profile.GetMinimumHeight();
            float maximum = profile.GetMaximumHeight();

            if (eventType == FloodEventType.Drought)
            {
                float severeRoll = DeterministicRoll(_worldName + "|Floods|SevereDrought|" + eventSeed.ToString(CultureInfo.InvariantCulture));
                severeDrought = severeRoll < Mathf.Clamp01(_config.SevereDroughtChance.Value);
                if (severeDrought)
                {
                    minimum = Mathf.Min(_config.SevereDroughtMinimumMeters.Value, _config.SevereDroughtMaximumMeters.Value);
                    maximum = Mathf.Max(_config.SevereDroughtMinimumMeters.Value, _config.SevereDroughtMaximumMeters.Value);
                }
            }

            float debugLimit = Mathf.Max(1f, _config.DebugMaximumSurgeMeters.Value);
            minimum = Mathf.Clamp(minimum, 0f, debugLimit);
            maximum = Mathf.Clamp(maximum, minimum, debugLimit);
            float roll = DeterministicRoll(_worldName + "|Floods|PeakHeight|" + eventType + "|" + eventSeed.ToString(CultureInfo.InvariantCulture));
            float magnitude = Mathf.Lerp(minimum, maximum, roll);
            return eventType == FloodEventType.Drought ? -magnitude : magnitude;
        }

        private float RollFlashSurgeDrawdownMeters(long eventSeed)
        {
            if (!_config.EnableFlashSurgeDrawdown.Value)
            {
                return 0f;
            }

            float minimum = Mathf.Clamp(_config.GetFlashSurgeDrawdownMinimum(), 0f, Mathf.Max(1f, _config.DebugMaximumSurgeMeters.Value));
            float maximum = Mathf.Clamp(_config.GetFlashSurgeDrawdownMaximum(), minimum, Mathf.Max(1f, _config.DebugMaximumSurgeMeters.Value));
            float roll = DeterministicRoll(_worldName + "|Floods|FlashDrawdown|" + eventSeed.ToString(CultureInfo.InvariantCulture));
            return Mathf.Lerp(minimum, maximum, roll);
        }

        private void AdvancePhase(double now)
        {
            FloodEventType type = _state.EventType == FloodEventType.None ? FloodEventType.Custom : _state.EventType;
            switch (_state.Phase)
            {
                case FloodPhase.Omen:
                    BeginPhase(type, FloodPhase.Rising, now, _state.ManualEvent, _state.PeakMeters, false);
                    break;
                case FloodPhase.Rising:
                    BeginPhase(type, FloodPhase.Peak, now, _state.ManualEvent, _state.PeakMeters, false);
                    break;
                case FloodPhase.Peak:
                    BeginPhase(type, FloodPhase.Receding, now, _state.ManualEvent, _state.PeakMeters, false);
                    break;
                case FloodPhase.Receding:
                    StopEvent(now, _state.ManualEvent);
                    break;
            }
        }

        private void StopEvent(double now, bool manualStop)
        {
            bool hadEvent = _state.Phase != FloodPhase.Dormant;
            FloodEventType completedType = _state.EventType;
            bool wasManual = _state.ManualEvent;
            long day = GetWorldDay(now);

            if (hadEvent && !wasManual)
            {
                SetLastCompletedDay(completedType, day);
                _state.LastCompletedDay = day;
                EnsureWaterCycleSchedule(now);
                ScheduleAfterNaturalEvent(completedType, now);
            }

            _state.Phase = FloodPhase.Dormant;
            _state.EventType = FloodEventType.None;
            _state.PhaseStartedWorldSeconds = now;
            _state.PhaseEndsWorldSeconds = 0d;
            _state.DrawdownMeters = 0f;
            _state.IsSevereDrought = false;
            _state.ManualEvent = false;
            _state.WildfireOriginX = 0f;
            _state.WildfireOriginY = 0f;
            _state.WildfireOriginZ = 0f;

            _water.RestoreOriginalWaterLevel();
            _environment.ReleaseForcedEnvironment();
            _wildfires.ExtinguishAll();

            if (hadEvent)
            {
                string ended = completedType == FloodEventType.Wildfire
                    ? (manualStop ? "The wildfire has been smothered." : "The wildfire burns itself out.")
                    : (manualStop ? "The Floods have been silenced." : "The waters begin to settle.");
                BroadcastMessage(ended);
            }

            SaveAndBroadcast();
        }

        private void ApplyLocalEffects(double now)
        {
            float surge = GetCurrentSurgeMeters(now);
            float storm = GetStormStrength(now);
            float fire = GetWildfireIntensity(now);
            _water.ApplySurge(surge);
            _environment.Apply(_state.Phase, storm);
            _environment.ApplyWildfire(fire);
            _lightning.UpdateAmbient(_state, storm);
            _wildfires.Update(_state, now, fire, IsGroundWetForFire(now, storm), IsPlayerWet(Player.m_localPlayer), _water, IsIgnitableForSpread, GetWindDirection);
        }

        internal float GetCurrentSurgeMeters(double now)
        {
            if (_state.Phase == FloodPhase.Dormant || _state.EventType == FloodEventType.Wildfire)
            {
                return 0f;
            }

            float p = PhaseProgress(now);
            switch (_state.Phase)
            {
                case FloodPhase.Omen:
                    // A Flash Surge begins with the water drawing back. The water remains
                    // at its maximum retreat at the exact handoff into Rising.
                    return _state.EventType == FloodEventType.FlashSurge && _state.DrawdownMeters > 0.001f
                        ? -_state.DrawdownMeters * Smooth01(p)
                        : 0f;
                case FloodPhase.Rising:
                    // Do not snap from the drawdown back to normal water. The rising curve
                    // starts at the withdrawn level and moves continuously into the surge.
                    return _state.EventType == FloodEventType.FlashSurge && _state.DrawdownMeters > 0.001f
                        ? Mathf.Lerp(-_state.DrawdownMeters, _state.PeakMeters, Smooth01(p))
                        : _state.PeakMeters * Smooth01(p);
                case FloodPhase.Peak:
                    return _state.PeakMeters;
                case FloodPhase.Receding:
                    return _state.PeakMeters * (1f - Smooth01(p));
                default:
                    return 0f;
            }
        }

        private float GetStormStrength(double now)
        {
            if (_state.Phase == FloodPhase.Dormant || _state.EventType == FloodEventType.Drought || _state.EventType == FloodEventType.Wildfire)
            {
                // Droughts and wildfires are calm, exposed-shore/fire events rather than thunderstorms.
                return 0f;
            }

            float p = PhaseProgress(now);
            float baseStrength;
            switch (_state.Phase)
            {
                case FloodPhase.Omen:
                    baseStrength = Mathf.Lerp(0.16f, 0.42f, Smooth01(p));
                    break;
                case FloodPhase.Rising:
                    baseStrength = Mathf.Lerp(0.45f, 0.94f, Smooth01(p));
                    break;
                case FloodPhase.Peak:
                    baseStrength = 1f;
                    break;
                case FloodPhase.Receding:
                    baseStrength = Mathf.Lerp(0.90f, 0.12f, Smooth01(p));
                    break;
                default:
                    baseStrength = 0f;
                    break;
            }

            float severity = _state.EventType == FloodEventType.StormTide ? 0.72f : 1f;
            return Mathf.Clamp01(baseStrength * severity);
        }

        private float GetWildfireIntensity(double now)
        {
            if (_state.Phase == FloodPhase.Dormant || _state.EventType != FloodEventType.Wildfire)
            {
                return 0f;
            }

            float p = PhaseProgress(now);
            switch (_state.Phase)
            {
                case FloodPhase.Omen:
                    return Mathf.Lerp(0.10f, 0.35f, Smooth01(p));
                case FloodPhase.Rising:
                    return Mathf.Lerp(0.38f, 0.90f, Smooth01(p));
                case FloodPhase.Peak:
                    return 1f;
                case FloodPhase.Receding:
                    return Mathf.Lerp(0.75f, 0.08f, Smooth01(p));
                default:
                    return 0f;
            }
        }

        private float PhaseProgress(double now)
        {
            double duration = _state.PhaseEndsWorldSeconds - _state.PhaseStartedWorldSeconds;
            if (duration <= 0.0001d)
            {
                return 1f;
            }

            return Mathf.Clamp01((float)((now - _state.PhaseStartedWorldSeconds) / duration));
        }

        private double GetPhaseDurationSeconds(FloodPhase phase)
        {
            FloodEventProfile profile = _config.GetProfile(_state.EventType);
            float days;
            if (profile == null)
            {
                switch (phase)
                {
                    case FloodPhase.Omen:
                        days = _config.OmenDays.Value;
                        break;
                    case FloodPhase.Rising:
                        days = _config.RisingDays.Value;
                        break;
                    case FloodPhase.Peak:
                        days = _config.PeakDays.Value;
                        break;
                    case FloodPhase.Receding:
                        days = _config.RecedingDays.Value;
                        break;
                    default:
                        days = 0f;
                        break;
                }
            }
            else
            {
                switch (phase)
                {
                    case FloodPhase.Omen:
                        days = profile.OmenDays.Value;
                        break;
                    case FloodPhase.Rising:
                        days = profile.RisingDays.Value;
                        break;
                    case FloodPhase.Peak:
                        days = profile.PeakDays.Value;
                        break;
                    case FloodPhase.Receding:
                        days = profile.RecedingDays.Value;
                        break;
                    default:
                        days = 0f;
                        break;
                }
            }

            return Math.Max(1d, days * Math.Max(60f, _config.GameDaySeconds.Value));
        }

        private void AnnouncePhase(FloodEventType type, FloodPhase phase)
        {
            float peak = _state.PeakMeters;
            string message;
            switch (type)
            {
                case FloodEventType.StormTide:
                    message = phase == FloodPhase.Omen ? "The wind turns cold. A storm tide is building."
                        : phase == FloodPhase.Rising ? (peak >= 4f ? "A violent tide tears across the low coast." : "The tide climbs over the shore.")
                        : phase == FloodPhase.Peak ? (peak >= 4f ? "The storm tide is tearing at the land." : "The storm tide has swallowed the low coast.")
                        : "The tide is turning. Keep clear of the shore.";
                    break;
                case FloodEventType.FlashSurge:
                    message = phase == FloodPhase.Omen ? "The sea pulls back. An unnatural swell gathers."
                        : phase == FloodPhase.Rising ? (peak >= 10f ? "A wall of water races toward the shore." : "A flash surge strikes the coast.")
                        : phase == FloodPhase.Peak ? (peak >= 10f ? "The coast is overwhelmed." : "The sea has leapt beyond the shore.")
                        : "The flash surge is losing its strength.";
                    break;
                case FloodEventType.GreatFlood:
                    message = phase == FloodPhase.Omen ? (peak >= 13f ? "A black wall of cloud rises beyond the sea. The Floods are coming." : "A black wall of cloud rises beyond the sea. The Great Flood is coming.")
                        : phase == FloodPhase.Rising ? (peak >= 13f ? "The sea is swallowing the land." : "The lowlands begin to drown.")
                        : phase == FloodPhase.Peak ? (peak >= 13f ? "The Floods have come." : peak >= 10f ? "The sea has claimed the lowlands." : "The lowlands lie beneath the storm tide.")
                        : "The storm weakens. The Great Flood begins to recede.";
                    break;
                case FloodEventType.Drought:
                    message = phase == FloodPhase.Omen
                        ? (_state.IsSevereDrought ? "The sea is falling back farther than it should. A severe drought is coming." : "The sea has begun to retreat. An unnatural drought is coming.")
                        : phase == FloodPhase.Rising
                            ? (_state.IsSevereDrought ? "The shoreline is racing away, exposing the seafloor." : "The shoreline pulls back, exposing the seafloor.")
                            : phase == FloodPhase.Peak
                                ? (_state.IsSevereDrought ? "The severe drought has laid the coast bare." : "The drought has laid the coast bare.")
                                : "The water is returning to the shore.";
                    break;
                case FloodEventType.Wildfire:
                    message = phase == FloodPhase.Omen ? "Smoke rises beyond the trees. The dry land is ready to burn."
                        : phase == FloodPhase.Rising ? "The wildfire is spreading through the dry ground."
                        : phase == FloodPhase.Peak ? "The wildfire is at its height. Fire races where water has been absent."
                        : "Rain, ash, and spent fuel begin to choke the wildfire.";
                    break;
                default:
                    message = phase == FloodPhase.Omen ? "A darkness gathers beyond the horizon. The Floods are coming."
                        : phase == FloodPhase.Rising ? "The lowlands begin to drown."
                        : phase == FloodPhase.Peak ? "The waters have reached their height."
                        : "The storm weakens. The Floods begin to recede.";
                    break;
            }

            BroadcastMessage(message);
        }

        private void BroadcastMessage(string message)
        {
            if (ZRoutedRpc.instance != null)
            {
                ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, RpcMessage, new object[] { message });
            }

            if (_config.DebugLogging.Value)
            {
                _logger.LogInfo("The Floods message: " + message);
            }
        }

        private void BroadcastState()
        {
            if (!IsAuthoritative || ZRoutedRpc.instance == null)
            {
                return;
            }

            _lastSyncAt = Time.unscaledTime;
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, RpcState, new object[] { _state.Serialize() });
        }

        private void SaveAndBroadcast()
        {
            if (IsAuthoritative && _loaded)
            {
                _store.Save(_worldName, _state);
                BroadcastState();
            }
        }

        private string DescribeSchedule(double now)
        {
            if (!_state.SchedulerInitialized)
            {
                return "The Floods: V0.8 scheduler is waiting for the world clock.";
            }

            double ordinaryMinutes = Math.Max(0d, _state.NextOrdinaryEventWorldSeconds - now) / 60d;
            double greatMinutes = Math.Max(0d, _state.NextGreatFloodWorldSeconds - now) / 60d;
            double forceMinutes = Math.Max(0d, _state.GreatFloodForceWorldSeconds - now) / 60d;
            double quietMinutes = Math.Max(0d, _state.GlobalRecoveryEndsWorldSeconds - now) / 60d;
            double wildfireMinutes = Math.Max(0d, _state.NextWildfireWorldSeconds - now) / 60d;
            double strikeSeconds = Math.Max(0d, _state.NextStrikeWorldSeconds - now);

            return "scheduler quiet=" + quietMinutes.ToString("0", CultureInfo.InvariantCulture)
                + "m nextOrdinary=" + ordinaryMinutes.ToString("0", CultureInfo.InvariantCulture)
                + "m nextGreatTarget=" + greatMinutes.ToString("0", CultureInfo.InvariantCulture)
                + "m greatForceBy=" + forceMinutes.ToString("0", CultureInfo.InvariantCulture)
                + "m nextWildfire=" + wildfireMinutes.ToString("0", CultureInfo.InvariantCulture)
                + "m nextStrike=" + strikeSeconds.ToString("0", CultureInfo.InvariantCulture) + "s"
                + "m cycle=" + _state.SchedulerCycle.ToString(CultureInfo.InvariantCulture);
        }

        private string DescribeState(double now)
        {
            long day = TryGetWorldSeconds(out now) ? GetWorldDay(now) : -1;
            string drawdown = _state.EventType == FloodEventType.FlashSurge && _state.DrawdownMeters > 0.001f
                ? " drawdown=" + _state.DrawdownMeters.ToString("0.00", CultureInfo.InvariantCulture) + "m"
                : string.Empty;
            return "event=" + GetEventDisplayName(_state.EventType)
                + " phase=" + _state.Phase
                + " day=" + day
                + " waterOffset=" + GetCurrentSurgeMeters(now).ToString("0.00", CultureInfo.InvariantCulture) + "m"
                + " peak=" + _state.PeakMeters.ToString("0.00", CultureInfo.InvariantCulture) + "m"
                + " dryness=" + _state.DrynessIndex.ToString("0.00", CultureInfo.InvariantCulture)
                + " fireNodes=" + _wildfires.ActiveNodeCount.ToString(CultureInfo.InvariantCulture)
                + (_state.EventType == FloodEventType.Drought ? (_state.IsSevereDrought ? " drought=severe" : " drought=standard") : string.Empty)
                + drawdown
                + GetActiveRangeDescription()
                + " storm=" + GetStormStrength(now).ToString("0.00", CultureInfo.InvariantCulture)
                + " wildfire=" + GetWildfireIntensity(now).ToString("0.00", CultureInfo.InvariantCulture)
                + " manual=" + _state.ManualEvent
                + " eventNumber=" + _state.EventNumber;
        }

        private string GetActiveRangeDescription()
        {
            FloodEventProfile profile = _config.GetProfile(_state.EventType);
            if (profile == null)
            {
                return string.Empty;
            }

            if (_state.EventType == FloodEventType.Wildfire)
            {
                return string.Empty;
            }

            if (_state.EventType == FloodEventType.Drought)
            {
                float minimum = _state.IsSevereDrought
                    ? Mathf.Min(_config.SevereDroughtMinimumMeters.Value, _config.SevereDroughtMaximumMeters.Value)
                    : profile.GetMinimumHeight();
                float maximum = _state.IsSevereDrought
                    ? Mathf.Max(_config.SevereDroughtMinimumMeters.Value, _config.SevereDroughtMaximumMeters.Value)
                    : profile.GetMaximumHeight();
                return " drawdownRange=" + minimum.ToString("0.0", CultureInfo.InvariantCulture)
                    + "–" + maximum.ToString("0.0", CultureInfo.InvariantCulture) + "m";
            }

            return " range=" + profile.GetMinimumHeight().ToString("0.0", CultureInfo.InvariantCulture)
                + "–" + profile.GetMaximumHeight().ToString("0.0", CultureInfo.InvariantCulture) + "m";
        }

        private long GetLastCompletedDay(FloodEventType type)
        {
            switch (type)
            {
                case FloodEventType.StormTide:
                    return _state.LastStormTideDay;
                case FloodEventType.FlashSurge:
                    return _state.LastFlashSurgeDay;
                case FloodEventType.GreatFlood:
                    return _state.LastGreatFloodDay;
                case FloodEventType.Drought:
                    return _state.LastDroughtDay;
                default:
                    return _state.LastCompletedDay;
            }
        }

        private void SetLastCompletedDay(FloodEventType type, long day)
        {
            switch (type)
            {
                case FloodEventType.StormTide:
                    _state.LastStormTideDay = day;
                    break;
                case FloodEventType.FlashSurge:
                    _state.LastFlashSurgeDay = day;
                    break;
                case FloodEventType.GreatFlood:
                    _state.LastGreatFloodDay = day;
                    break;
                case FloodEventType.Drought:
                    _state.LastDroughtDay = day;
                    break;
            }
        }

        private bool TryGetWorldSeconds(out double seconds)
        {
            seconds = 0d;
            if (ZNet.instance == null)
            {
                return false;
            }

            try
            {
                DateTime networkTime = ZNet.instance.GetTime();
                seconds = networkTime.Ticks / (double)TimeSpan.TicksPerSecond;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private long GetWorldDay(double worldSeconds)
        {
            try
            {
                if (EnvMan.instance != null)
                {
                    MethodInfo getDay = AccessTools.Method(typeof(EnvMan), "GetCurrentDay", Type.EmptyTypes);
                    if (getDay != null)
                    {
                        object result = getDay.Invoke(EnvMan.instance, null);
                        if (result != null)
                        {
                            return Convert.ToInt64(result, CultureInfo.InvariantCulture);
                        }
                    }
                }
            }
            catch
            {
                // Safe deterministic fallback below.
            }

            return Math.Max(1L, (long)Math.Floor(worldSeconds / Math.Max(60f, _config.GameDaySeconds.Value)) + 1L);
        }

        private void LogWaterProbe()
        {
            string[] names = { "ZoneSystem", "WaterVolume", "WaterSurface", "Ocean", "Floating", "Ship", "EnvMan" };
            _logger.LogInfo("[TheFloods] WATER PROBE BEGIN");

            foreach (string name in names)
            {
                Type type = AccessTools.TypeByName(name);
                if (type == null)
                {
                    _logger.LogInfo("[TheFloods] WATER PROBE type missing: " + name);
                    continue;
                }

                IEnumerable<string> fields = type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(f => HasWaterKeyword(f.Name))
                    .Select(f => f.FieldType.Name + " " + f.Name);

                IEnumerable<string> methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(m => HasWaterKeyword(m.Name))
                    .Select(m => m.ReturnType.Name + " " + m.Name + "(" + string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name)) + ")");

                _logger.LogInfo("[TheFloods] WATER PROBE " + name + " fields: " + string.Join(" || ", fields.ToArray()));
                _logger.LogInfo("[TheFloods] WATER PROBE " + name + " methods: " + string.Join(" || ", methods.ToArray()));
            }

            _logger.LogInfo("[TheFloods] WATER PROBE " + _water.BaselineDescription + " applied=" + _water.AppliedDescription);
            Vector3 sample = Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : Vector3.zero;
            _water.LogSurfaceProbe(sample);
            _logger.LogInfo("[TheFloods] WATER PROBE END");
        }

        private static bool HasWaterKeyword(string text)
        {
            string s = text.ToLowerInvariant();
            return s.Contains("water") || s.Contains("ocean") || s.Contains("surface") || s.Contains("wave") || s.Contains("liquid") || s.Contains("env") || s.Contains("force");
        }

        private static FloodPhase ParsePhase(string raw)
        {
            string text = (raw ?? string.Empty).Trim().ToLowerInvariant();
            switch (text)
            {
                case "omen":
                    return FloodPhase.Omen;
                case "rise":
                case "rising":
                case "spread":
                case "spreading":
                    return FloodPhase.Rising;
                case "peak":
                case "high":
                    return FloodPhase.Peak;
                case "recede":
                case "receding":
                case "burnout":
                case "burningout":
                    return FloodPhase.Receding;
                default:
                    return FloodPhase.Omen;
            }
        }

        private static bool TryParseEventType(string raw, out FloodEventType type)
        {
            string text = (raw ?? string.Empty).Trim().ToLowerInvariant().Replace(" ", string.Empty).Replace("_", string.Empty).Replace("-", string.Empty);
            switch (text)
            {
                case "stormtide":
                case "tide":
                    type = FloodEventType.StormTide;
                    return true;
                case "flashsurge":
                case "surge":
                    type = FloodEventType.FlashSurge;
                    return true;
                case "greatflood":
                case "flood":
                    type = FloodEventType.GreatFlood;
                    return true;
                case "drought":
                case "lowtide":
                    type = FloodEventType.Drought;
                    return true;
                case "wildfire":
                case "fire":
                    type = FloodEventType.Wildfire;
                    return true;
                case "custom":
                    type = FloodEventType.Custom;
                    return true;
                default:
                    type = FloodEventType.None;
                    return false;
            }
        }

        private string GetEventDisplayName(FloodEventType type)
        {
            FloodEventProfile profile = _config.GetProfile(type);
            if (profile != null)
            {
                return profile.DisplayName;
            }

            return type == FloodEventType.Custom ? "Custom" : "None";
        }

        private static string FormatSignedMeters(float value)
        {
            string sign = value >= 0f ? "+" : "-";
            return sign + Mathf.Abs(value).ToString("0.00", CultureInfo.InvariantCulture) + "m";
        }

        private static float Smooth01(float value)
        {
            value = Mathf.Clamp01(value);
            return value * value * (3f - 2f * value);
        }

        private static long StableHash(string text)
        {
            unchecked
            {
                long hash = 1469598103934665603L;
                for (int i = 0; i < text.Length; i++)
                {
                    hash ^= text[i];
                    hash *= 1099511628211L;
                }

                return hash;
            }
        }

        private static float DeterministicRoll(string source)
        {
            long h = StableHash(source);
            ulong positive = (ulong)h;
            return (positive % 1000000UL) / 1000000f;
        }
    }

    /*
     * REPORT BACK TO CLAUDE - V0.9.1 lightning/fire runtime notes:
     * - Lightning uses a procedural LineRenderer + short-range Light. No vanilla thunder SFX prefab is required; audio is deliberately skipped if no stable clip is found.
     * - Fire uses fallback ParticleSystems with a generated soft radial texture, Custom/LitParticles first, additive flame, alpha-blended smoke, capped low-intensity lights, and temporary scorch marks.
     * - Reflection probes used by the director: Heightmap.FindBiome(Vector3), EnvMan.GetCurrentEnvironment().m_name, EnvMan.IsWet(), Player.GetAllPlayers(), Player.InShelter(), Player.GetSEMan(), SEMan.HaveStatusEffect(int|string), Player.GetPlayerID/m_playerID.
     * - Lightning damage is HitData.m_damage.m_lightning, point=strike, dir=Vector3.down, push=configured. The receiving client applies Damage only when the payload player id matches its local player.
     * - Burning status is intentionally not reflected in V0.9.1; wildfire harm is direct HitData.m_damage.m_fire ticks. Live fire nodes are runtime-only and re-seed from FloodState.WildfireOrigin after load.
     */
    internal sealed class FloodLightningSystem
    {
        private sealed class ActiveBolt
        {
            internal GameObject Root;
            internal LineRenderer Line;
            internal Light Light;
            internal float CreatedAt;
            internal float EndsAt;
            internal float Seed;
        }

        private readonly BepInEx.Logging.ManualLogSource _logger;
        private readonly FloodConfig _config;
        private readonly FloodVisuals _visuals;
        private readonly List<ActiveBolt> _bolts = new List<ActiveBolt>();
        private Material _boltMaterial;
        private float _nextAmbientAt;

        internal FloodLightningSystem(BepInEx.Logging.ManualLogSource logger, FloodConfig config, FloodVisuals visuals)
        {
            _logger = logger;
            _config = config;
            _visuals = visuals;
        }

        internal void UpdateAmbient(FloodState state, float stormStrength)
        {
            UpdateBolts();

            if (!_config.EnableLightningStrikes.Value || !_config.EnableAmbientBolts.Value || !_config.EnableDistantLightningFlashes.Value)
            {
                return;
            }

            if (state == null || state.Phase == FloodPhase.Dormant || state.Phase == FloodPhase.Omen || stormStrength < 0.25f || Player.m_localPlayer == null)
            {
                return;
            }

            if (Time.unscaledTime < _nextAmbientAt)
            {
                return;
            }

            float rate = Mathf.Max(0.1f, _config.AmbientBoltsPerMinuteAtPeak.Value) * Mathf.Clamp01(stormStrength);
            float baseGap = Mathf.Clamp(60f / rate, 4f, 90f);
            _nextAmbientAt = Time.unscaledTime + UnityEngine.Random.Range(baseGap * 0.55f, baseGap * 1.45f);

            Vector3 point = PickAmbientPoint(Player.m_localPlayer.transform.position);
            SpawnBolt(point, stormStrength, false, UnityEngine.Random.Range(int.MinValue, int.MaxValue));
            float distance = Vector3.Distance(Player.m_localPlayer.transform.position, point);
            float distanceScale = Mathf.Clamp01(1f - distance / 180f);
            _visuals.TriggerLightningFlash(Mathf.Lerp(0.04f, 0.20f, stormStrength) * Mathf.Lerp(0.45f, 1f, distanceScale), 0.16f);
        }

        internal void RenderStrike(LightningStrikePayload strike)
        {
            if (strike == null)
            {
                return;
            }

            SpawnBolt(strike.Position, strike.IsDirectHit ? 1f : 0.82f, strike.IsDirectHit, (int)(strike.Seed & 0x7fffffff));
            float flash = strike.IsDirectHit ? 0.45f : 0.24f;
            if (Player.m_localPlayer != null)
            {
                float distance = Vector3.Distance(Player.m_localPlayer.transform.position, strike.Position);
                flash *= Mathf.Clamp01(1.15f - distance / 160f);
            }

            _visuals.TriggerLightningFlash(Mathf.Clamp(flash, 0.06f, 0.55f), strike.IsDirectHit ? 0.24f : 0.18f);
        }

        internal void Dispose()
        {
            for (int i = _bolts.Count - 1; i >= 0; i--)
            {
                DestroyBolt(_bolts[i]);
            }

            _bolts.Clear();
            if (_boltMaterial != null)
            {
                UnityEngine.Object.Destroy(_boltMaterial);
                _boltMaterial = null;
            }
        }

        private Vector3 PickAmbientPoint(Vector3 center)
        {
            float angle = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
            float radius = UnityEngine.Random.Range(45f, 135f);
            Vector3 candidate = center + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
            Vector3 grounded;
            return TryFindGround(candidate, out grounded) ? grounded : candidate;
        }

        private void SpawnBolt(Vector3 impact, float strength, bool direct, int seed)
        {
            try
            {
                while (_bolts.Count >= 3)
                {
                    DestroyBolt(_bolts[0]);
                    _bolts.RemoveAt(0);
                }

                GameObject root = new GameObject("TheFloods_LightningBolt");
                LineRenderer line = root.AddComponent<LineRenderer>();
                line.useWorldSpace = true;
                line.alignment = LineAlignment.View;
                line.material = GetBoltMaterial();
                line.startColor = direct ? new Color(0.95f, 0.98f, 1f, 1f) : new Color(0.74f, 0.86f, 1f, 0.95f);
                line.endColor = new Color(0.48f, 0.68f, 1f, 0.88f);
                line.startWidth = Mathf.Lerp(0.18f, 0.48f, Mathf.Clamp01(strength));
                line.endWidth = Mathf.Lerp(0.08f, 0.22f, Mathf.Clamp01(strength));
                line.positionCount = direct ? 16 : 12;

                System.Random random = new System.Random(seed == 0 ? Environment.TickCount : seed);
                Vector3 top = impact + Vector3.up * Lerp(random, 120f, 190f);
                for (int i = 0; i < line.positionCount; i++)
                {
                    float t = i / (float)(line.positionCount - 1);
                    Vector3 point = Vector3.Lerp(top, impact, t);
                    float jitter = (1f - t) * (direct ? 9f : 14f);
                    point += new Vector3(Lerp(random, -jitter, jitter), 0f, Lerp(random, -jitter, jitter));
                    line.SetPosition(i, point);
                }

                Light light = root.AddComponent<Light>();
                light.type = LightType.Point;
                light.color = new Color(0.72f, 0.84f, 1f, 1f);
                light.range = Mathf.Clamp(_config.BoltLightRange.Value, 12f, 60f) * Mathf.Lerp(0.55f, 0.9f, Mathf.Clamp01(strength));
                light.intensity = Mathf.Clamp(_config.BoltLightIntensity.Value, 0.5f, 8f) * Mathf.Lerp(0.55f, 0.95f, Mathf.Clamp01(strength));
                root.transform.position = impact;

                float duration = Mathf.Clamp(_config.BoltDurationSeconds.Value, 0.06f, 0.75f);
                _bolts.Add(new ActiveBolt
                {
                    Root = root,
                    Line = line,
                    Light = light,
                    CreatedAt = Time.unscaledTime,
                    EndsAt = Time.unscaledTime + duration,
                    Seed = seed * 0.001f
                });
            }
            catch (Exception ex)
            {
                if (_config.DebugLogging.Value)
                {
                    _logger.LogWarning("[TheFloods] Lightning bolt render failed: " + ex.Message);
                }
            }
        }

        private void UpdateBolts()
        {
            for (int i = _bolts.Count - 1; i >= 0; i--)
            {
                ActiveBolt bolt = _bolts[i];
                if (bolt == null || bolt.Root == null || Time.unscaledTime >= bolt.EndsAt)
                {
                    DestroyBolt(bolt);
                    _bolts.RemoveAt(i);
                    continue;
                }

                float total = Mathf.Max(0.01f, bolt.EndsAt - bolt.CreatedAt);
                float age = Mathf.Clamp01((Time.unscaledTime - bolt.CreatedAt) / total);
                bool visible = Mathf.Sin((Time.unscaledTime + bolt.Seed) * 95f) > -0.55f || age < 0.18f;
                if (bolt.Line != null)
                {
                    bolt.Line.enabled = visible;
                }

                if (bolt.Light != null)
                {
                    bolt.Light.intensity *= Mathf.Lerp(0.35f, 0.82f, age);
                }
            }
        }

        private void DestroyBolt(ActiveBolt bolt)
        {
            if (bolt != null && bolt.Root != null)
            {
                UnityEngine.Object.Destroy(bolt.Root);
            }
        }

        private Material GetBoltMaterial()
        {
            if (_boltMaterial != null)
            {
                return _boltMaterial;
            }

            Shader shader = Shader.Find("Custom/LitParticles");
            if (shader == null)
            {
                shader = Shader.Find("Sprites/Default");
            }

            if (shader == null)
            {
                shader = Shader.Find("Unlit/Color");
            }

            _boltMaterial = new Material(shader);
            _boltMaterial.color = new Color(0.75f, 0.88f, 1f, 1f);
            ConfigureBlend(_boltMaterial);
            return _boltMaterial;
        }

        private static void ConfigureBlend(Material material)
        {
            if (material == null)
            {
                return;
            }

            TrySetInt(material, "_SrcMode", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            TrySetInt(material, "_DstMode", (int)UnityEngine.Rendering.BlendMode.One);
            TrySetInt(material, "_ZWrite", 0);
            TrySetInt(material, "_Mode", 2);
            material.renderQueue = 3000;
        }

        private static void TrySetInt(Material material, string property, int value)
        {
            try
            {
                if (material.HasProperty(property))
                {
                    material.SetInt(property, value);
                }
            }
            catch
            {
                // Shader property names vary between Valheim/Unity builds.
            }
        }

        private static bool TryFindGround(Vector3 candidate, out Vector3 grounded)
        {
            grounded = candidate;
            bool foundTerrain = false;
            try
            {
                Type zs = AccessTools.TypeByName("ZoneSystem");
                if (zs != null)
                {
                    object inst = AccessTools.Property(zs, "instance")?.GetValue(null) ?? AccessTools.Field(zs, "m_instance")?.GetValue(null);
                    if (inst != null)
                    {
                        MethodInfo getGroundHeight = AccessTools.Method(zs, "GetGroundHeight", new[] { typeof(Vector3) });
                        if (getGroundHeight != null)
                        {
                            object res = getGroundHeight.Invoke(inst, new object[] { candidate });
                            if (res is float h && h > -1000f)
                            {
                                grounded = new Vector3(candidate.x, h, candidate.z);
                                foundTerrain = true;
                            }
                        }
                    }
                }
            }
            catch { }

            try
            {
                Vector3 start = new Vector3(candidate.x, candidate.y + 140f, candidate.z);
                if (foundTerrain) return true;

                // FIX: Picking the highest point prevents Ashlands/Bog Witch deep seabed issues
                RaycastHit[] hits = Physics.RaycastAll(start, Vector3.down, 300f, ~0, QueryTriggerInteraction.Ignore);
                if (hits.Length > 0)
                {
                    RaycastHit highest = hits[0];
                    for (int i = 1; i < hits.Length; i++)
                    {
                        if (hits[i].point.y > highest.point.y)
                        {
                            highest = hits[i];
                        }
                    }
                    grounded = highest.point;
                    return true;
                }
            }
            catch
            {
                // Use the ungrounded point.
            }

            return foundTerrain;
        }

        private static float Lerp(System.Random random, float min, float max)
        {
            return Mathf.Lerp(min, max, (float)random.NextDouble());
        }
    }

    internal sealed class WildfireSystem
    {
        private sealed class FireNode
        {
            internal Vector3 Position;
            internal float Radius;
            internal float TargetRadius;
            internal float RemainingSeconds;
            internal float NextSpreadAt;
            internal float CreatedAt;
            internal GameObject Root;
            internal Light Light;
            internal ParticleSystem Particles;
            internal ParticleSystem SmokeParticles;
        }

        private sealed class ScorchMark
        {
            internal GameObject Root;
            internal float EndsAt;
        }

        private readonly BepInEx.Logging.ManualLogSource _logger;
        private readonly FloodConfig _config;
        private readonly List<FireNode> _nodes = new List<FireNode>();
        private readonly List<ScorchMark> _scorchMarks = new List<ScorchMark>();
        private Material _fireMaterial;
        private Material _smokeMaterial;
        private Material _scorchMaterial;
        private Texture2D _softParticleTexture;
        private long _seededEvent;
        private float _nextPlayerDamageAt;

        internal WildfireSystem(BepInEx.Logging.ManualLogSource logger, FloodConfig config)
        {
            _logger = logger;
            _config = config;
        }

        internal int ActiveNodeCount
        {
            get { return _nodes.Count; }
        }

        internal void Ignite(Vector3 point, double now, long seed)
        {
            AddNode(point, Mathf.Max(0.1f, _config.NodeLifeSeconds.Value), seed);
        }

        internal void Update(FloodState state, double now, float intensity, bool rainWet, bool localPlayerWet, FloodWaterAdapter water, Func<Vector3, bool> canSpread, Func<Vector3> windProvider)
        {
            UpdateScorchMarks();

            if (state != null && state.EventType == FloodEventType.Wildfire && state.Phase != FloodPhase.Dormant)
            {
                if (_seededEvent != state.EventSeed || _nodes.Count == 0)
                {
                    _seededEvent = state.EventSeed;
                    Vector3 origin = new Vector3(state.WildfireOriginX, state.WildfireOriginY, state.WildfireOriginZ);
                    if (origin != Vector3.zero)
                    {
                        AddNode(origin, Mathf.Max(0.1f, _config.NodeLifeSeconds.Value), state.EventSeed);
                    }
                }
            }

            if (_nodes.Count == 0)
            {
                return;
            }

            bool eventActive = state != null && state.EventType == FloodEventType.Wildfire && state.Phase != FloodPhase.Dormant;
            bool spreadAllowed = eventActive && (state.Phase == FloodPhase.Rising || state.Phase == FloodPhase.Peak);
            bool burnout = rainWet || (eventActive && state.Phase == FloodPhase.Receding) || (eventActive && intensity < 0.08f);
            float dt = Mathf.Max(0.001f, Time.deltaTime);

            for (int i = _nodes.Count - 1; i >= 0; i--)
            {
                FireNode node = _nodes[i];
                if (node == null)
                {
                    _nodes.RemoveAt(i);
                    continue;
                }

                bool underWater = water != null && water.IsPointUnderKnownWater(node.Position);
                float drain = burnout || underWater ? dt * 5.5f : dt;
                node.RemainingSeconds -= drain;
                node.Radius = Mathf.MoveTowards(node.Radius, node.TargetRadius, dt * Mathf.Max(0.4f, node.TargetRadius * 0.55f));
                UpdateVisual(node, intensity, burnout || underWater);

                if (node.RemainingSeconds <= 0f)
                {
                    BurnOutNode(node);
                    _nodes.RemoveAt(i);
                    continue;
                }

                if (spreadAllowed && _nodes.Count < GetEffectiveMaxNodes() && Time.unscaledTime >= node.NextSpreadAt)
                {
                    node.NextSpreadAt = Time.unscaledTime + Mathf.Max(1f, _config.SpreadIntervalSeconds.Value);
                    TrySpread(node, canSpread, windProvider);
                }
            }

            UpdateFireLightBudget();
            ApplyPlayerDamage(localPlayerWet, water);
        }

        internal void ExtinguishAll()
        {
            for (int i = _nodes.Count - 1; i >= 0; i--)
            {
                DestroyNode(_nodes[i]);
            }

            _nodes.Clear();
            _seededEvent = 0L;
        }

        internal void Dispose()
        {
            ExtinguishAll();
            for (int i = _scorchMarks.Count - 1; i >= 0; i--)
            {
                DestroyScorch(_scorchMarks[i]);
            }

            _scorchMarks.Clear();
            if (_fireMaterial != null)
            {
                UnityEngine.Object.Destroy(_fireMaterial);
                _fireMaterial = null;
            }

            if (_smokeMaterial != null)
            {
                UnityEngine.Object.Destroy(_smokeMaterial);
                _smokeMaterial = null;
            }

            if (_scorchMaterial != null)
            {
                UnityEngine.Object.Destroy(_scorchMaterial);
                _scorchMaterial = null;
            }

            if (_softParticleTexture != null)
            {
                UnityEngine.Object.Destroy(_softParticleTexture);
                _softParticleTexture = null;
            }
        }

        private void TrySpread(FireNode parent, Func<Vector3, bool> canSpread, Func<Vector3> windProvider)
        {
            if (UnityEngine.Random.value > Mathf.Clamp01(_config.SpreadChance.Value))
            {
                return;
            }

            Vector3 wind = windProvider == null ? Vector3.forward : windProvider();
            wind.y = 0f;
            if (wind.sqrMagnitude < 0.001f)
            {
                wind = UnityEngine.Random.insideUnitSphere;
                wind.y = 0f;
            }

            wind = wind.sqrMagnitude < 0.001f ? Vector3.forward : wind.normalized;
            float bias = Mathf.Clamp01(_config.DownwindBias.Value);
            float maxAngle = Mathf.Lerp(105f, 38f, bias);
            float angle = UnityEngine.Random.Range(-maxAngle, maxAngle);
            Vector3 direction = Quaternion.Euler(0f, angle, 0f) * wind;
            if (UnityEngine.Random.value > bias)
            {
                Vector3 side = Quaternion.Euler(0f, UnityEngine.Random.value < 0.5f ? -82f : 82f, 0f) * wind;
                direction = Vector3.Slerp(direction, side, UnityEngine.Random.Range(0.20f, 0.55f)).normalized;
            }

            if (Vector3.Dot(direction, wind) < -0.05f)
            {
                direction = Vector3.Slerp(direction, wind, 0.75f).normalized;
            }

            float step = Mathf.Max(1.5f, _config.SpreadStepMeters.Value);
            Vector3 candidate = parent.Position + direction * UnityEngine.Random.Range(step * 0.72f, step * 1.28f);
            Vector3 grounded;
            if (!TryFindGround(candidate, out grounded))
            {
                grounded = candidate;
            }

            if (canSpread != null && !canSpread(grounded))
            {
                return;
            }

            AddNode(grounded, Mathf.Max(0.1f, _config.NodeLifeSeconds.Value) * UnityEngine.Random.Range(0.72f, 1.18f), Environment.TickCount);
        }

        private void ApplyPlayerDamage(bool wet, FloodWaterAdapter water)
        {
            if (Player.m_localPlayer == null || Time.unscaledTime < _nextPlayerDamageAt)
            {
                return;
            }

            Player player = Player.m_localPlayer;
            bool protectedByWater = wet || (water != null && water.IsPointUnderKnownWater(player.transform.position));
            if (protectedByWater)
            {
                return;
            }

            for (int i = 0; i < _nodes.Count; i++)
            {
                FireNode node = _nodes[i];
                if (node == null)
                {
                    continue;
                }

                if ((player.transform.position - node.Position).sqrMagnitude <= node.Radius * node.Radius)
                {
                    _nextPlayerDamageAt = Time.unscaledTime + Mathf.Max(0.25f, _config.FireDamageIntervalSeconds.Value);
                    HitData hit = new HitData();
                    hit.m_damage.m_fire = Mathf.Max(0f, _config.PlayerFireDamagePerTick.Value);
                    hit.m_point = player.transform.position;
                    hit.m_dir = (player.transform.position - node.Position).normalized;
                    hit.m_pushForce = 0f;
                    hit.m_hitType = HitData.HitType.Undefined;
                    player.Damage(hit);
                    return;
                }
            }
        }

        private void AddNode(Vector3 point, float lifeSeconds, long seed)
        {
            if (_nodes.Count >= GetEffectiveMaxNodes())
            {
                return;
            }

            float targetRadius = GetEffectiveNodeRadius();
            FireNode node = new FireNode
            {
                Position = point,
                Radius = Mathf.Max(0.35f, targetRadius * 0.32f),
                TargetRadius = targetRadius,
                RemainingSeconds = lifeSeconds,
                CreatedAt = Time.unscaledTime,
                NextSpreadAt = Time.unscaledTime + UnityEngine.Random.Range(1f, Mathf.Max(1f, _config.SpreadIntervalSeconds.Value))
            };

            CreateNodeVisual(node, seed);
            _nodes.Add(node);
        }

        private void CreateNodeVisual(FireNode node, long seed)
        {
            try
            {
                GameObject root = new GameObject("TheFloods_WildfireNode");
                root.transform.position = node.Position;

                ParticleSystem particles = root.AddComponent<ParticleSystem>();
                ParticleSystem.MainModule main = particles.main;
                main.loop = true;
                main.startLifetime = new ParticleSystem.MinMaxCurve(0.6f, 1.4f);
                main.startSpeed = new ParticleSystem.MinMaxCurve(2.0f, 4.5f);
                main.startSize = new ParticleSystem.MinMaxCurve(0.7f, 1.6f);
                main.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 0.6f, 0.18f, 0.95f), new Color(1f, 0.9f, 0.45f, 0.85f));
                main.maxParticles = 200;
                main.simulationSpace = ParticleSystemSimulationSpace.World;

                ParticleSystem.EmissionModule emission = particles.emission;
                emission.rateOverTime = new ParticleSystem.MinMaxCurve(55f, 90f);

                ParticleSystem.ShapeModule shape = particles.shape;
                shape.shapeType = ParticleSystemShapeType.Cone;
                shape.radius = Mathf.Max(0.25f, node.TargetRadius * 0.35f);
                shape.angle = 12f;

                ParticleSystem.ColorOverLifetimeModule colorLife = particles.colorOverLifetime;
                colorLife.enabled = true;
                Gradient flameGradient = new Gradient();
                flameGradient.SetKeys(
                    new[]
                    {
                        new GradientColorKey(new Color(1f, 0.92f, 0.55f), 0f),
                        new GradientColorKey(new Color(1f, 0.35f, 0.07f), 0.52f),
                        new GradientColorKey(new Color(0.45f, 0.10f, 0.03f), 1f)
                    },
                    new[]
                    {
                        new GradientAlphaKey(0.90f, 0f),
                        new GradientAlphaKey(0.72f, 0.45f),
                        new GradientAlphaKey(0f, 1f)
                    });
                colorLife.color = new ParticleSystem.MinMaxGradient(flameGradient);

                ParticleSystem.SizeOverLifetimeModule sizeLife = particles.sizeOverLifetime;
                sizeLife.enabled = true;
                sizeLife.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(
                    new Keyframe(0f, 0.75f),
                    new Keyframe(0.35f, 1.0f),
                    new Keyframe(1f, 0.20f)));

                ParticleSystem.VelocityOverLifetimeModule velocityLife = particles.velocityOverLifetime;
                velocityLife.enabled = true;
                velocityLife.space = ParticleSystemSimulationSpace.World;
                velocityLife.y = new ParticleSystem.MinMaxCurve(1.6f, 2.6f);

                ParticleSystemRenderer renderer = particles.GetComponent<ParticleSystemRenderer>();
                if (renderer != null)
                {
                    renderer.material = GetFireMaterial();
                    renderer.renderMode = ParticleSystemRenderMode.Billboard;
                }

                ParticleSystem smoke = CreateSmokeSystem(root, node);

                Light light = root.AddComponent<Light>();
                light.type = LightType.Point;
                light.color = new Color(1f, 0.38f, 0.10f, 1f);
                light.range = Mathf.Max(3f, node.TargetRadius * 1.5f);
                light.intensity = 0.75f;

                node.Root = root;
                node.Light = light;
                node.Particles = particles;
                node.SmokeParticles = smoke;
            }
            catch (Exception ex)
            {
                if (_config.DebugLogging.Value)
                {
                    _logger.LogWarning("[TheFloods] Wildfire fallback VFX failed: " + ex.Message);
                }
            }
        }

        private ParticleSystem CreateSmokeSystem(GameObject root, FireNode node)
        {
            GameObject smokeObject = new GameObject("Smoke");
            smokeObject.transform.SetParent(root.transform, false);
            smokeObject.transform.localPosition = Vector3.up * 0.65f;

            ParticleSystem smoke = smokeObject.AddComponent<ParticleSystem>();
            ParticleSystem.MainModule main = smoke.main;
            main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(2.4f, 4.6f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.5f, 1.4f);
            main.startSize = new ParticleSystem.MinMaxCurve(2.2f, 4.5f);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.12f, 0.11f, 0.10f, 0.5f), new Color(0.34f, 0.31f, 0.27f, 0.38f));
            main.maxParticles = 140;
            main.simulationSpace = ParticleSystemSimulationSpace.World;

            ParticleSystem.EmissionModule emission = smoke.emission;
            emission.rateOverTime = new ParticleSystem.MinMaxCurve(16f, 28f);

            ParticleSystem.ShapeModule shape = smoke.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.radius = Mathf.Max(0.45f, node.TargetRadius * 0.55f);
            shape.angle = 18f;

            ParticleSystem.ColorOverLifetimeModule colorLife = smoke.colorOverLifetime;
            colorLife.enabled = true;
            Gradient smokeGradient = new Gradient();
            smokeGradient.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(0.10f, 0.095f, 0.085f), 0f),
                    new GradientColorKey(new Color(0.28f, 0.26f, 0.23f), 1f)
                },
                new[]
                {
                    new GradientAlphaKey(0.0f, 0f),
                    new GradientAlphaKey(0.42f, 0.28f),
                    new GradientAlphaKey(0f, 1f)
                });
            colorLife.color = new ParticleSystem.MinMaxGradient(smokeGradient);

            ParticleSystem.SizeOverLifetimeModule sizeLife = smoke.sizeOverLifetime;
            sizeLife.enabled = true;
            sizeLife.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(
                new Keyframe(0f, 0.55f),
                new Keyframe(0.55f, 1.1f),
                new Keyframe(1f, 1.45f)));

            ParticleSystem.VelocityOverLifetimeModule velocityLife = smoke.velocityOverLifetime;
            velocityLife.enabled = true;
            velocityLife.space = ParticleSystemSimulationSpace.World;
            velocityLife.y = new ParticleSystem.MinMaxCurve(0.8f, 1.7f);
            velocityLife.x = new ParticleSystem.MinMaxCurve(-0.18f, 0.18f);
            velocityLife.z = new ParticleSystem.MinMaxCurve(-0.18f, 0.18f);

            ParticleSystemRenderer renderer = smoke.GetComponent<ParticleSystemRenderer>();
            if (renderer != null)
            {
                renderer.material = GetSmokeMaterial();
                renderer.renderMode = ParticleSystemRenderMode.Billboard;
            }

            return smoke;
        }

        private void UpdateVisual(FireNode node, float intensity, bool suppressed)
        {
            if (node == null)
            {
                return;
            }

            if (node.Light != null)
            {
                float flicker = UnityEngine.Random.Range(0.72f, 1.18f);
                node.Light.range = Mathf.Max(2.5f, node.Radius * 1.5f);
                node.Light.intensity = (suppressed ? 0.18f : 0.78f) * Mathf.Lerp(0.65f, 1.15f, Mathf.Clamp01(intensity)) * flicker;
            }

            if (node.Particles != null)
            {
                ParticleSystem.EmissionModule emission = node.Particles.emission;
                emission.rateOverTime = suppressed
                    ? new ParticleSystem.MinMaxCurve(3f, 8f)
                    : new ParticleSystem.MinMaxCurve(30f, 55f);
            }

            if (node.SmokeParticles != null)
            {
                ParticleSystem.EmissionModule emission = node.SmokeParticles.emission;
                emission.rateOverTime = suppressed
                    ? new ParticleSystem.MinMaxCurve(12f, 22f)
                    : new ParticleSystem.MinMaxCurve(8f, 16f);
            }
        }

        private void DestroyNode(FireNode node)
        {
            if (node != null && node.Root != null)
            {
                UnityEngine.Object.Destroy(node.Root);
            }
        }

        private void BurnOutNode(FireNode node)
        {
            if (node != null)
            {
                CreateScorchMark(node.Position, node.TargetRadius);
            }

            DestroyNode(node);
        }

        private void UpdateFireLightBudget()
        {
            int enabled = 0;
            Vector3 playerPosition = Player.m_localPlayer == null ? Vector3.zero : Player.m_localPlayer.transform.position;
            for (int i = 0; i < _nodes.Count; i++)
            {
                FireNode node = _nodes[i];
                if (node == null || node.Light == null)
                {
                    continue;
                }

                bool near = Player.m_localPlayer == null || (playerPosition - node.Position).sqrMagnitude <= 55f * 55f;
                bool shouldLight = near && enabled < 8;
                node.Light.enabled = shouldLight;
                if (shouldLight)
                {
                    enabled++;
                }
            }
        }

        private int GetEffectiveMaxNodes()
        {
            return Mathf.Clamp(Mathf.Max(_config.MaxFireNodes.Value, 60), 1, 80);
        }

        private float GetEffectiveNodeRadius()
        {
            return Mathf.Clamp(_config.NodeRadius.Value, 1.5f, 3.0f);
        }

        private void CreateScorchMark(Vector3 position, float radius)
        {
            try
            {
                while (_scorchMarks.Count >= 60)
                {
                    DestroyScorch(_scorchMarks[0]);
                    _scorchMarks.RemoveAt(0);
                }

                GameObject scorch = GameObject.CreatePrimitive(PrimitiveType.Quad);
                scorch.name = "TheFloods_ScorchMark";
                scorch.transform.position = position + Vector3.up * 0.035f;
                scorch.transform.rotation = Quaternion.Euler(90f, UnityEngine.Random.Range(0f, 360f), 0f);
                float scale = Mathf.Max(1.8f, radius * UnityEngine.Random.Range(1.7f, 2.6f));
                scorch.transform.localScale = new Vector3(scale, scale, 1f);

                Collider collider = scorch.GetComponent<Collider>();
                if (collider != null)
                {
                    UnityEngine.Object.Destroy(collider);
                }

                MeshRenderer renderer = scorch.GetComponent<MeshRenderer>();
                if (renderer != null)
                {
                    renderer.material = GetScorchMaterial();
                }

                _scorchMarks.Add(new ScorchMark
                {
                    Root = scorch,
                    EndsAt = Time.unscaledTime + UnityEngine.Random.Range(90f, 150f)
                });
            }
            catch (Exception ex)
            {
                if (_config.DebugLogging.Value)
                {
                    _logger.LogWarning("[TheFloods] Scorch mark failed: " + ex.Message);
                }
            }
        }

        private void UpdateScorchMarks()
        {
            for (int i = _scorchMarks.Count - 1; i >= 0; i--)
            {
                ScorchMark mark = _scorchMarks[i];
                if (mark == null || mark.Root == null || Time.unscaledTime >= mark.EndsAt)
                {
                    DestroyScorch(mark);
                    _scorchMarks.RemoveAt(i);
                }
            }
        }

        private void DestroyScorch(ScorchMark mark)
        {
            if (mark != null && mark.Root != null)
            {
                UnityEngine.Object.Destroy(mark.Root);
            }
        }

        private Material GetFireMaterial()
        {
            if (_fireMaterial != null)
            {
                return _fireMaterial;
            }

            Shader shader = Shader.Find("Custom/LitParticles");
            if (shader == null)
            {
                shader = Shader.Find("Particles/Standard Unlit");
            }

            if (shader == null)
            {
                shader = Shader.Find("Sprites/Default");
            }

            _fireMaterial = new Material(shader);
            _fireMaterial.mainTexture = GetSoftParticleTexture();
            _fireMaterial.color = new Color(1f, 0.42f, 0.06f, 0.85f);
            ConfigureBlend(_fireMaterial, true);
            return _fireMaterial;
        }

        private Material GetSmokeMaterial()
        {
            if (_smokeMaterial != null)
            {
                return _smokeMaterial;
            }

            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null)
            {
                shader = Shader.Find("Particles/Standard Unlit");
            }

            _smokeMaterial = new Material(shader);
            _smokeMaterial.mainTexture = GetSoftParticleTexture();
            _smokeMaterial.color = new Color(0.25f, 0.23f, 0.20f, 0.24f);
            ConfigureBlend(_smokeMaterial, false);
            return _smokeMaterial;
        }

        private Material GetScorchMaterial()
        {
            if (_scorchMaterial != null)
            {
                return _scorchMaterial;
            }

            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null)
            {
                shader = Shader.Find("Unlit/Transparent");
            }

            _scorchMaterial = new Material(shader);
            _scorchMaterial.mainTexture = GetSoftParticleTexture();
            _scorchMaterial.color = new Color(0.025f, 0.020f, 0.016f, 0.34f);
            ConfigureBlend(_scorchMaterial, false);
            return _scorchMaterial;
        }

        private Texture2D GetSoftParticleTexture()
        {
            if (_softParticleTexture != null)
            {
                return _softParticleTexture;
            }

            const int size = 64;
            _softParticleTexture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            _softParticleTexture.wrapMode = TextureWrapMode.Clamp;
            _softParticleTexture.filterMode = FilterMode.Bilinear;
            Vector2 center = new Vector2((size - 1) * 0.5f, (size - 1) * 0.5f);
            float radius = size * 0.48f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float distance = Vector2.Distance(new Vector2(x, y), center);
                    float alpha = Mathf.Clamp01(1f - distance / radius);
                    alpha = Mathf.SmoothStep(0f, 1f, alpha);
                    _softParticleTexture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }

            _softParticleTexture.Apply(false, true);
            return _softParticleTexture;
        }

        private static void ConfigureBlend(Material material, bool additive)
        {
            if (material == null)
            {
                return;
            }

            TrySetInt(material, "_SrcMode", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            TrySetInt(material, "_DstMode", additive ? (int)UnityEngine.Rendering.BlendMode.One : (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            TrySetInt(material, "_ZWrite", 0);
            TrySetInt(material, "_Mode", additive ? 2 : 3);
            material.renderQueue = 3000;
        }

        private static void TrySetInt(Material material, string property, int value)
        {
            try
            {
                if (material.HasProperty(property))
                {
                    material.SetInt(property, value);
                }
            }
            catch
            {
                // Shader property names vary between Valheim/Unity builds.
            }
        }

        private static bool TryFindGround(Vector3 candidate, out Vector3 grounded)
        {
            grounded = candidate;
            bool foundTerrain = false;
            try
            {
                Type zs = AccessTools.TypeByName("ZoneSystem");
                if (zs != null)
                {
                    object inst = AccessTools.Property(zs, "instance")?.GetValue(null) ?? AccessTools.Field(zs, "m_instance")?.GetValue(null);
                    if (inst != null)
                    {
                        MethodInfo getGroundHeight = AccessTools.Method(zs, "GetGroundHeight", new[] { typeof(Vector3) });
                        if (getGroundHeight != null)
                        {
                            object res = getGroundHeight.Invoke(inst, new object[] { candidate });
                            if (res is float h && h > -1000f)
                            {
                                grounded = new Vector3(candidate.x, h, candidate.z);
                                foundTerrain = true;
                            }
                        }
                    }
                }
            }
            catch { }

            try
            {
                Vector3 start = new Vector3(candidate.x, candidate.y + 140f, candidate.z);
                if (foundTerrain) return true;

                // FIX: Picking the highest point prevents Ashlands/Bog Witch deep seabed issues
                RaycastHit[] hits = Physics.RaycastAll(start, Vector3.down, 300f, ~0, QueryTriggerInteraction.Ignore);
                if (hits.Length > 0)
                {
                    RaycastHit highest = hits[0];
                    for (int i = 1; i < hits.Length; i++)
                    {
                        if (hits[i].point.y > highest.point.y)
                        {
                            highest = hits[i];
                        }
                    }
                    grounded = highest.point;
                    return true;
                }
            }
            catch
            {
                // Use candidate.
            }

            return foundTerrain;
        }
    }

    internal sealed class FloodWaterAdapter
    {
        private sealed class OceanTileBinding
        {
            internal WaterVolume Volume;
            internal Heightmap Heightmap;
            internal MeshRenderer Renderer;
            internal Collider Collider;
            internal Transform RendererTransform;
            internal Transform ColliderTransform;
            internal Vector3 RendererBaselineLocalPosition;
            internal Vector3 RendererBaselineWorldPosition;
            internal Vector3 ColliderBaselineLocalPosition;
            internal Vector3 ColliderCenterBaseline;
            internal float SurfaceOffsetBaseline;
            internal bool CanMoveRendererTransform;
            internal bool CanMoveColliderTransform;
            internal bool CanMoveColliderCenter;
            internal string DebugName;
        }

        private readonly BepInEx.Logging.ManualLogSource _logger;
        private readonly FloodConfig _config;
        private readonly Dictionary<int, OceanTileBinding> _oceanTiles = new Dictionary<int, OceanTileBinding>();

        private FieldInfo _heightmapField;
        private FieldInfo _waterSurfaceField;
        private FieldInfo _colliderField;
        private FieldInfo _surfaceOffsetField;
        private bool _fieldsSearched;
        private bool _fieldsValid;
        private bool _fieldFailureLogged;
        private float _currentSurge;
        private float _lastScanAt = -999f;
        private bool _loggedActive;
        private bool _physicsSyncPending;

        internal FloodWaterAdapter(BepInEx.Logging.ManualLogSource logger, FloodConfig config)
        {
            _logger = logger;
            _config = config;
        }

        internal string BaselineDescription
        {
            get
            {
                return "ocean terrain-water tiles=" + _oceanTiles.Count.ToString(CultureInfo.InvariantCulture);
            }
        }

        internal string AppliedDescription
        {
            get
            {
                return _currentSurge.ToString("0.000", CultureInfo.InvariantCulture) + "m";
            }
        }

        internal bool IsPointUnderKnownWater(Vector3 point)
        {
            if (!_config.EnableLiveWaterLevel.Value)
            {
                return false;
            }

            DiscoverOceanTiles(false);
            foreach (OceanTileBinding binding in _oceanTiles.Values)
            {
                if (binding == null || binding.Volume == null)
                {
                    continue;
                }

                float surface = SafeReadSurface(binding.Volume, point);
                if (!float.IsNaN(surface) && point.y <= surface + 0.15f)
                {
                    return true;
                }
            }

            return false;
        }

        internal void ApplySurge(float surgeMeters)
        {
            if (!_config.EnableLiveWaterLevel.Value)
            {
                RestoreOriginalWaterLevel();
                return;
            }

            float limit = Mathf.Max(1f, _config.DebugMaximumSurgeMeters.Value);
            _currentSurge = Mathf.Clamp(surgeMeters, -limit, limit);
            DiscoverOceanTiles(false);
            ApplyFloodToBoundTiles();

            if (Mathf.Abs(_currentSurge) > 0.001f && !_loggedActive)
            {
                _loggedActive = true;
                _logger.LogInfo("[TheFloods] Terrain-water adapter active. Ocean tile surface offsets, visible water meshes, and water triggers now follow the signed water offset. No terrain was edited.");
            }
        }

        internal void RestoreOriginalWaterLevel()
        {
            _currentSurge = 0f;
            ApplyFloodToBoundTiles();
            _loggedActive = false;
        }

        internal void Dispose()
        {
            RestoreOriginalWaterLevel();
            SyncPhysicsIfNeeded();
        }

        // WaterVolume updates its child renderer during its own native Update cycle.
        // This hook runs immediately afterward so the storm offset survives until rendering.
        internal void ReapplyAfterNativeUpdate(WaterVolume volume)
        {
            // Kept for compatibility with old saves and diagnostic hooks.
            // WaterVolume.UpdateWaterTime is static in this Valheim build, so
            // it cannot provide a real tile instance. Per-frame methods below
            // are the authoritative visual sync path.
            if (volume == null)
            {
                return;
            }

            OceanTileBinding binding;
            if (_oceanTiles.TryGetValue(volume.GetInstanceID(), out binding) && binding != null)
            {
                ApplyRendererLift(binding);
                ApplyColliderLift(binding);
            }
        }

        internal void ReapplyForFrame()
        {
            if (!_config.EnableLiveWaterLevel.Value)
            {
                return;
            }

            DiscoverOceanTiles(false);
            ApplyFloodToBoundTiles();
        }

        internal void ReapplyVisualsForRender()
        {
            if (!_config.EnableLiveWaterLevel.Value)
            {
                return;
            }

            foreach (OceanTileBinding binding in _oceanTiles.Values)
            {
                if (binding == null || binding.Volume == null || binding.RendererTransform == null)
                {
                    continue;
                }

                ApplyRendererLift(binding);
            }
        }

        internal void SyncPhysicsIfNeeded()
        {
            if (!_physicsSyncPending)
            {
                return;
            }

            _physicsSyncPending = false;
            Physics.SyncTransforms();
        }

        internal void LogSurfaceProbe(Vector3 samplePoint)
        {
            DiscoverOceanTiles(true);

            int totalVolumes = 0;
            int terrainWaterVolumes = 0;
            int rendererLiftable = 0;
            int colliderLiftable = 0;
            int colliderCenterLiftable = 0;

            try
            {
                WaterVolume[] all = Resources.FindObjectsOfTypeAll<WaterVolume>();
                totalVolumes = all.Length;
                for (int i = 0; i < all.Length; i++)
                {
                    WaterVolume volume = all[i];
                    if (IsOceanTerrainWater(volume, out Heightmap ignored))
                    {
                        terrainWaterVolumes++;
                    }
                }
            }
            catch
            {
                // The detailed bound-tile information below is still useful.
            }

            foreach (OceanTileBinding binding in _oceanTiles.Values)
            {
                if (binding == null)
                {
                    continue;
                }

                if (binding.CanMoveRendererTransform)
                {
                    rendererLiftable++;
                }

                if (binding.CanMoveColliderTransform)
                {
                    colliderLiftable++;
                }

                if (binding.CanMoveColliderCenter)
                {
                    colliderCenterLiftable++;
                }
            }

            _logger.LogInfo("[TheFloods] SURFACE PROBE BEGIN sample=" + FormatVector(samplePoint)
                + " surge=" + _currentSurge.ToString("0.000", CultureInfo.InvariantCulture)
                + " allWaterVolumes=" + totalVolumes.ToString(CultureInfo.InvariantCulture)
                + " terrainWaterVolumes=" + terrainWaterVolumes.ToString(CultureInfo.InvariantCulture)
                + " boundTiles=" + _oceanTiles.Count.ToString(CultureInfo.InvariantCulture)
                + " rendererLiftable=" + rendererLiftable.ToString(CultureInfo.InvariantCulture)
                + " colliderTransformLiftable=" + colliderLiftable.ToString(CultureInfo.InvariantCulture)
                + " colliderCenterLiftable=" + colliderCenterLiftable.ToString(CultureInfo.InvariantCulture));

            int printed = 0;
            foreach (OceanTileBinding binding in _oceanTiles.Values)
            {
                if (binding == null || binding.Volume == null)
                {
                    continue;
                }

                float surface = SafeReadSurface(binding.Volume, samplePoint);
                float currentOffset = GetSurfaceOffset(binding.Volume, binding.SurfaceOffsetBaseline);
                string rendererInfo = binding.RendererTransform == null
                    ? "none"
                    : ("name='" + binding.RendererTransform.gameObject.name
                        + "' y=" + binding.RendererTransform.position.y.ToString("0.000", CultureInfo.InvariantCulture)
                        + " safe=" + binding.CanMoveRendererTransform);
                string colliderInfo = binding.Collider == null
                    ? "none"
                    : ("type=" + binding.Collider.GetType().Name
                        + " y=" + binding.Collider.transform.position.y.ToString("0.000", CultureInfo.InvariantCulture)
                        + " transformSafe=" + binding.CanMoveColliderTransform
                        + " centerSafe=" + binding.CanMoveColliderCenter);

                _logger.LogInfo("[TheFloods] SURFACE TILE name='" + binding.DebugName
                    + " id=" + binding.Volume.GetInstanceID()
                    + " volumeY=" + binding.Volume.transform.position.y.ToString("0.000", CultureInfo.InvariantCulture)
                    + " surfaceOffset=" + currentOffset.ToString("0.000", CultureInfo.InvariantCulture)
                    + " baselineOffset=" + binding.SurfaceOffsetBaseline.ToString("0.000", CultureInfo.InvariantCulture)
                    + " sampledSurface=" + surface.ToString("0.000", CultureInfo.InvariantCulture)
                    + " renderer=" + rendererInfo
                    + " collider=" + colliderInfo);

                printed++;
                if (printed >= 12)
                {
                    break;
                }
            }

            if (_oceanTiles.Count == 0)
            {
                _logger.LogWarning("[TheFloods] SURFACE PROBE found zero terrain-linked WaterVolume tiles. Do not test more flood heights yet; send this probe so the classification can be widened safely.");
            }

            _logger.LogInfo("[TheFloods] SURFACE PROBE END");
        }

        private void DiscoverOceanTiles(bool force)
        {
            if (!force && Time.unscaledTime - _lastScanAt < Mathf.Max(0.5f, _config.WaterSurfaceScanSeconds.Value))
            {
                return;
            }

            _lastScanAt = Time.unscaledTime;
            if (!FindFields())
            {
                return;
            }

            try
            {
                WaterVolume[] allVolumes = Resources.FindObjectsOfTypeAll<WaterVolume>();
                for (int i = 0; i < allVolumes.Length; i++)
                {
                    WaterVolume volume = allVolumes[i];
                    if (!IsOceanTerrainWater(volume, out Heightmap heightmap))
                    {
                        continue;
                    }

                    int id = volume.GetInstanceID();
                    if (_oceanTiles.ContainsKey(id))
                    {
                        continue;
                    }

                    MeshRenderer renderer = _waterSurfaceField.GetValue(volume) as MeshRenderer;
                    Collider collider = _colliderField.GetValue(volume) as Collider;
                    Transform rendererTransform = renderer == null ? null : renderer.transform;
                    Transform colliderTransform = collider == null ? null : collider.transform;

                    OceanTileBinding binding = new OceanTileBinding
                    {
                        Volume = volume,
                        Heightmap = heightmap,
                        Renderer = renderer,
                        Collider = collider,
                        RendererTransform = rendererTransform,
                        ColliderTransform = colliderTransform,
                        RendererBaselineLocalPosition = rendererTransform == null ? Vector3.zero : rendererTransform.localPosition,
                        RendererBaselineWorldPosition = rendererTransform == null ? Vector3.zero : rendererTransform.position,
                        ColliderBaselineLocalPosition = colliderTransform == null ? Vector3.zero : colliderTransform.localPosition,
                        ColliderCenterBaseline = GetColliderCenter(collider),
                        SurfaceOffsetBaseline = GetSurfaceOffset(volume, 0f),
                        CanMoveRendererTransform = CanMoveWaterChild(rendererTransform, heightmap),
                        CanMoveColliderTransform = CanMoveWaterChild(colliderTransform, heightmap),
                        CanMoveColliderCenter = CanOffsetColliderCenter(collider),
                        DebugName = volume.gameObject == null ? "unknown" : volume.gameObject.name
                    };

                    _oceanTiles.Add(id, binding);

                    if (_config.VerboseWaterTileBindingLogs.Value)
                    {
                        _logger.LogInfo("[TheFloods] Bound terrain-water tile name='" + binding.DebugName
                            + "' id=" + id.ToString(CultureInfo.InvariantCulture)
                            + " renderer='" + (rendererTransform == null ? "none" : rendererTransform.gameObject.name)
                            + "' rendererSafe=" + binding.CanMoveRendererTransform
                            + " collider='" + (colliderTransform == null ? "none" : colliderTransform.gameObject.name)
                            + "' colliderTransformSafe=" + binding.CanMoveColliderTransform
                            + " colliderCenterSafe=" + binding.CanMoveColliderCenter
                            + " baseSurfaceOffset=" + binding.SurfaceOffsetBaseline.ToString("0.000", CultureInfo.InvariantCulture));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[TheFloods] Terrain-water discovery failed: " + ex.Message);
            }
        }

        private bool IsOceanTerrainWater(WaterVolume volume, out Heightmap heightmap)
        {
            heightmap = null;
            if (volume == null || volume.gameObject == null || !volume.gameObject.scene.IsValid())
            {
                return false;
            }

            if (!FindFields())
            {
                return false;
            }

            try
            {
                if (volume.GetLiquidType() != LiquidType.Water)
                {
                    return false;
                }

                heightmap = _heightmapField.GetValue(volume) as Heightmap;
                return heightmap != null;
            }
            catch
            {
                return false;
            }
        }

        private void ApplyFloodToBoundTiles()
        {
            if (!FindFields())
            {
                return;
            }

            List<int> invalid = null;
            foreach (KeyValuePair<int, OceanTileBinding> pair in _oceanTiles)
            {
                OceanTileBinding binding = pair.Value;
                if (binding == null || binding.Volume == null || binding.Volume.gameObject == null)
                {
                    if (invalid == null)
                    {
                        invalid = new List<int>();
                    }

                    invalid.Add(pair.Key);
                    continue;
                }

                try
                {
                    float surfaceOffset = _config.EnableWaterSurfaceQueryPatch.Value
                        ? binding.SurfaceOffsetBaseline + _currentSurge
                        : binding.SurfaceOffsetBaseline;
                    _surfaceOffsetField.SetValue(binding.Volume, surfaceOffset);
                    ApplyRendererLift(binding);
                    ApplyColliderLift(binding);
                }
                catch (Exception ex)
                {
                    if (_config.DebugLogging.Value)
                    {
                        _logger.LogWarning("[TheFloods] Could not update water tile " + pair.Key.ToString(CultureInfo.InvariantCulture) + ": " + ex.Message);
                    }
                }
            }

            if (invalid != null)
            {
                for (int i = 0; i < invalid.Count; i++)
                {
                    _oceanTiles.Remove(invalid[i]);
                }
            }
        }

        private void ApplyRendererLift(OceanTileBinding binding)
        {
            if (!_config.EnableOceanRendererLift.Value || binding.RendererTransform == null || !binding.CanMoveRendererTransform)
            {
                return;
            }

            // Use world space. These mesh children are reset by Valheim during
            // terrain/water refreshes; world space prevents a parent reset from
            // leaving the visible sea beneath the physical water height.
            Vector3 desiredWorld = binding.RendererBaselineWorldPosition + Vector3.up * _currentSurge;
            if ((binding.RendererTransform.position - desiredWorld).sqrMagnitude > 0.000001f)
            {
                binding.RendererTransform.position = desiredWorld;
            }
        }

        private void ApplyColliderLift(OceanTileBinding binding)
        {
            if (!_config.EnableWaterSurfaceQueryPatch.Value || binding.Collider == null)
            {
                return;
            }

            // The main ocean BoxCollider lives on the WaterVolume itself in
            // this Valheim build. Moving that transform would add the surge a
            // second time because GetWaterSurface already uses m_surfaceOffset.
            // Move the collider centre instead so trigger/swimming detection
            // follows the visible and queried water level exactly.
            bool colliderIsVolumeTransform = binding.ColliderTransform != null
                && binding.Volume != null
                && binding.ColliderTransform == binding.Volume.transform;

            if (!colliderIsVolumeTransform && binding.ColliderTransform != null && binding.CanMoveColliderTransform)
            {
                Vector3 desired = binding.ColliderBaselineLocalPosition + Vector3.up * _currentSurge;
                if ((binding.ColliderTransform.localPosition - desired).sqrMagnitude > 0.000001f)
                {
                    binding.ColliderTransform.localPosition = desired;
                    _physicsSyncPending = true;
                }

                return;
            }

            if (binding.CanMoveColliderCenter)
            {
                Vector3 desiredCenter = binding.ColliderCenterBaseline + Vector3.up * _currentSurge;
                if ((GetColliderCenter(binding.Collider) - desiredCenter).sqrMagnitude > 0.000001f)
                {
                    SetColliderCenter(binding.Collider, desiredCenter);
                    _physicsSyncPending = true;
                }
            }
        }

        private bool FindFields()
        {
            if (_fieldsSearched)
            {
                return _fieldsValid;
            }

            _fieldsSearched = true;
            _heightmapField = AccessTools.Field(typeof(WaterVolume), "m_heightmap");
            _waterSurfaceField = AccessTools.Field(typeof(WaterVolume), "m_waterSurface");
            _colliderField = AccessTools.Field(typeof(WaterVolume), "m_collider");
            _surfaceOffsetField = AccessTools.Field(typeof(WaterVolume), "m_surfaceOffset");

            _fieldsValid = _heightmapField != null && _waterSurfaceField != null && _colliderField != null && _surfaceOffsetField != null;
            if (!_fieldsValid && !_fieldFailureLogged)
            {
                _fieldFailureLogged = true;
                _logger.LogWarning("[TheFloods] Required WaterVolume fields were not found. Terrain-water flooding is disabled safely. Found heightmap="
                    + (_heightmapField != null) + " renderer=" + (_waterSurfaceField != null)
                    + " collider=" + (_colliderField != null) + " surfaceOffset=" + (_surfaceOffsetField != null));
            }

            return _fieldsValid;
        }

        private static bool CanMoveWaterChild(Transform candidate, Heightmap heightmap)
        {
            if (candidate == null)
            {
                return false;
            }

            if (heightmap == null || heightmap.transform == null)
            {
                return true;
            }

            Transform terrainTransform = heightmap.transform;
            if (candidate == terrainTransform)
            {
                return false;
            }

            // Moving a parent of terrain would move land. Moving a child of terrain moves only the water child.
            return !terrainTransform.IsChildOf(candidate);
        }

        private static bool CanOffsetColliderCenter(Collider collider)
        {
            return collider is BoxCollider || collider is SphereCollider || collider is CapsuleCollider;
        }

        private static Vector3 GetColliderCenter(Collider collider)
        {
            BoxCollider box = collider as BoxCollider;
            if (box != null)
            {
                return box.center;
            }

            SphereCollider sphere = collider as SphereCollider;
            if (sphere != null)
            {
                return sphere.center;
            }

            CapsuleCollider capsule = collider as CapsuleCollider;
            if (capsule != null)
            {
                return capsule.center;
            }

            return Vector3.zero;
        }

        private static void SetColliderCenter(Collider collider, Vector3 center)
        {
            BoxCollider box = collider as BoxCollider;
            if (box != null)
            {
                box.center = center;
                return;
            }

            SphereCollider sphere = collider as SphereCollider;
            if (sphere != null)
            {
                sphere.center = center;
                return;
            }

            CapsuleCollider capsule = collider as CapsuleCollider;
            if (capsule != null)
            {
                capsule.center = center;
            }
        }

        private float GetSurfaceOffset(WaterVolume volume, float fallback)
        {
            try
            {
                object value = _surfaceOffsetField.GetValue(volume);
                if (value is float)
                {
                    return (float)value;
                }
            }
            catch
            {
                // The caller uses the safe baseline/fallback.
            }

            return fallback;
        }

        private static float SafeReadSurface(WaterVolume volume, Vector3 point)
        {
            try
            {
                return volume.GetWaterSurface(point, 1f);
            }
            catch
            {
                return float.NaN;
            }
        }

        private static string FormatVector(Vector3 value)
        {
            return "(" + value.x.ToString("0.0", CultureInfo.InvariantCulture)
                + "," + value.y.ToString("0.0", CultureInfo.InvariantCulture)
                + "," + value.z.ToString("0.0", CultureInfo.InvariantCulture) + ")";
        }
    }


    internal sealed class FloodEnvironmentAdapter
    {
        private readonly BepInEx.Logging.ManualLogSource _logger;
        private readonly FloodConfig _config;
        private MethodInfo _forceMethod;
        private bool _methodSearched;
        private bool _forceRequested;
        private string _lastRequestedEnvironment = string.Empty;
        private float _lastAttempt;
        private bool _failureLogged;

        internal FloodEnvironmentAdapter(BepInEx.Logging.ManualLogSource logger, FloodConfig config)
        {
            _logger = logger;
            _config = config;
        }

        internal void Apply(FloodPhase phase, float stormStrength)
        {
            if (!_config.EnableNativeThunderstorm.Value)
            {
                ReleaseForcedEnvironment();
                return;
            }

            if (EnvMan.instance == null || !FindForceMethod())
            {
                return;
            }

            // Keep the omen visually distant and eerie. Native rain, thunder and
            // lightning begin only once the storm has genuinely arrived.
            bool stormShouldBeActive = phase != FloodPhase.Omen
                && stormStrength >= Mathf.Clamp01(_config.NativeStormStartStrength.Value);

            if (stormShouldBeActive)
            {
                string requested = _config.NativeStormEnvironment.Value ?? "ThunderStorm";
                if (_forceRequested && string.Equals(_lastRequestedEnvironment, requested, StringComparison.Ordinal) && Time.unscaledTime - _lastAttempt < 25f)
                {
                    return;
                }

                TrySet(requested);
                return;
            }

            if (_forceRequested)
            {
                ReleaseForcedEnvironment();
            }
        }

        internal void ApplyWildfire(float wildfireIntensity)
        {
            if (wildfireIntensity <= 0.001f)
            {
                return;
            }

            string requested = _config.WildfireForcedEnvironment.Value ?? string.Empty;
            if (string.IsNullOrWhiteSpace(requested) || EnvMan.instance == null || !FindForceMethod())
            {
                return;
            }

            if (_forceRequested && string.Equals(_lastRequestedEnvironment, requested, StringComparison.Ordinal) && Time.unscaledTime - _lastAttempt < 25f)
            {
                return;
            }

            TrySet(requested);
        }

        internal void ReleaseForcedEnvironment()
        {
            if (!_forceRequested || EnvMan.instance == null || !FindForceMethod())
            {
                return;
            }

            TrySet(string.Empty);
            _forceRequested = false;
            _lastRequestedEnvironment = string.Empty;
        }

        private bool FindForceMethod()
        {
            if (_methodSearched)
            {
                return _forceMethod != null;
            }

            _methodSearched = true;
            _forceMethod = AccessTools.Method(typeof(EnvMan), "SetForceEnvironment", new[] { typeof(string) });
            if (_forceMethod == null && !_failureLogged)
            {
                _failureLogged = true;
                _logger.LogWarning("[TheFloods] Environment adapter could not find EnvMan.SetForceEnvironment(string). The black storm bank will still work.");
            }

            return _forceMethod != null;
        }

        private void TrySet(string environmentName)
        {
            try
            {
                _forceMethod.Invoke(EnvMan.instance, new object[] { environmentName });
                bool changed = !string.Equals(_lastRequestedEnvironment, environmentName ?? string.Empty, StringComparison.Ordinal);
                _forceRequested = !string.IsNullOrEmpty(environmentName);
                _lastRequestedEnvironment = environmentName ?? string.Empty;
                _lastAttempt = Time.unscaledTime;
                if (changed && _config.DebugLogging.Value)
                {
                    _logger.LogInfo("[TheFloods] Environment adapter requested '" + (string.IsNullOrEmpty(environmentName) ? "natural weather" : environmentName) + "'.");
                }
            }
            catch (Exception ex)
            {
                if (!_failureLogged)
                {
                    _failureLogged = true;
                    _logger.LogWarning("[TheFloods] Environment adapter failed: " + ex.Message);
                }
            }
        }
    }

    internal sealed class FloodVisuals
    {
        private readonly FloodConfig _config;
        private Texture2D _skyGradient;
        private Texture2D _stormBank;
        private float _lightningEndsAt;
        private float _lightningStrength;
        private float _lightningDuration = 0.12f;

        internal FloodVisuals(FloodConfig config)
        {
            _config = config;
        }

        internal void Draw(FloodState state, float stormStrength, float wildfireIntensity, FloodConfig config)
        {
            if (state == null || state.Phase == FloodPhase.Dormant || (stormStrength <= 0.001f && wildfireIntensity <= 0.001f && _lightningEndsAt <= Time.unscaledTime))
            {
                return;
            }

            if (ZNet.instance != null && ZNet.instance.IsDedicated())
            {
                return;
            }

            if (Player.m_localPlayer == null)
            {
                return;
            }

            EnsureTextures();
            Color old = GUI.color;

            try
            {
                float alpha = Mathf.Clamp01(stormStrength * config.SkyDarkening.Value);
                if (wildfireIntensity > 0.001f)
                {
                    alpha = Mathf.Max(alpha, Mathf.Clamp01(wildfireIntensity * config.WildfireSkyTintStrength.Value));
                }

                if (alpha > 0.001f)
                {
                    // V0.6 used several partial-screen GUI rectangles for the black storm bank
                    // and low mist. Their edges showed up as dark transparent bands on some
                    // displays and render paths. V0.6.1 uses one full-screen, smoothly fading
                    // sky tint only. Native Valheim weather supplies the real clouds, rain and
                    // thunder until a future world-space storm-wall renderer is ready.
                    GUI.color = state.EventType == FloodEventType.Wildfire
                        ? new Color(0.30f, 0.12f, 0.045f, alpha * 0.58f)
                        : new Color(0.028f, 0.045f, 0.078f, alpha * 0.62f);
                    GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), _skyGradient);

                    if (state.EventType == FloodEventType.Wildfire && _stormBank != null)
                    {
                        GUI.color = new Color(0.18f, 0.16f, 0.14f, alpha * 0.35f);
                        float height = Mathf.Clamp(Screen.height * 0.38f, 120f, Screen.height);
                        GUI.DrawTextureWithTexCoords(new Rect(0f, 0f, Screen.width, height), _stormBank, new Rect(Time.unscaledTime * 0.006f, 0f, 1.8f, 1f));
                    }
                }

                if (_lightningEndsAt > Time.unscaledTime)
                {
                    float remaining = Mathf.Clamp01((_lightningEndsAt - Time.unscaledTime) / Mathf.Max(0.03f, _lightningDuration));
                    GUI.color = new Color(0.78f, 0.86f, 1f, _lightningStrength * remaining);
                    GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
                }
            }
            finally
            {
                GUI.color = old;
            }
        }

        internal void TriggerLightningFlash(float strength, float duration)
        {
            if (!_config.EnableDistantLightningFlashes.Value)
            {
                return;
            }

            _lightningStrength = Mathf.Max(_lightningStrength, Mathf.Clamp01(strength));
            _lightningDuration = Mathf.Clamp(duration, 0.03f, 0.8f);
            _lightningEndsAt = Mathf.Max(_lightningEndsAt, Time.unscaledTime + _lightningDuration);
        }

        internal void Dispose()
        {
            if (_skyGradient != null)
            {
                UnityEngine.Object.Destroy(_skyGradient);
                _skyGradient = null;
            }

            if (_stormBank != null)
            {
                UnityEngine.Object.Destroy(_stormBank);
                _stormBank = null;
            }
        }

        private void EnsureTextures()
        {
            if (_skyGradient == null)
            {
                _skyGradient = new Texture2D(1, 128, TextureFormat.RGBA32, false);
                _skyGradient.wrapMode = TextureWrapMode.Clamp;
                _skyGradient.filterMode = FilterMode.Bilinear;

                for (int y = 0; y < 128; y++)
                {
                    // GUI texture coordinates run from the top of the screen downward here.
                    // Keep the upper sky dark but fade continuously to transparent before the
                    // ground, with no hard rectangular cutoff.
                    float t = y / 127f;
                    float alpha = Mathf.Clamp01(1f - t / 0.82f);
                    alpha = Mathf.SmoothStep(0f, 1f, alpha);
                    _skyGradient.SetPixel(0, y, new Color(1f, 1f, 1f, alpha));
                }

                _skyGradient.Apply(false, true);
            }

            if (_stormBank == null)
            {
                const int width = 512;
                const int height = 128;
                _stormBank = new Texture2D(width, height, TextureFormat.RGBA32, false);
                _stormBank.wrapMode = TextureWrapMode.Repeat;
                _stormBank.filterMode = FilterMode.Bilinear;

                for (int y = 0; y < height; y++)
                {
                    float vertical = y / (float)(height - 1);
                    for (int x = 0; x < width; x++)
                    {
                        float a = Mathf.PerlinNoise(x * 0.0125f, y * 0.045f);
                        float b = Mathf.PerlinNoise((x + 173) * 0.024f, (y + 41) * 0.085f);
                        float cloud = Mathf.Clamp01((a * 0.72f + b * 0.28f) - vertical * 0.58f + 0.12f);
                        float edge = Mathf.SmoothStep(0f, 1f, cloud);
                        float alpha = Mathf.Clamp01(edge * Mathf.Lerp(0.96f, 0.18f, vertical));
                        _stormBank.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                    }
                }

                _stormBank.Apply(false, true);
            }
        }
    }

    internal static class TerminalReflection
    {
        internal static string ReadInput(Terminal terminal)
        {
            try
            {
                object input = GetInputObject(terminal);
                if (input == null)
                {
                    return string.Empty;
                }

                PropertyInfo text = AccessTools.Property(input.GetType(), "text");
                return text == null ? string.Empty : text.GetValue(input, null) as string ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        internal static void ClearInput(Terminal terminal)
        {
            try
            {
                object input = GetInputObject(terminal);
                if (input == null)
                {
                    return;
                }

                PropertyInfo text = AccessTools.Property(input.GetType(), "text");
                if (text != null && text.CanWrite)
                {
                    text.SetValue(input, string.Empty, null);
                }
            }
            catch
            {
                // A failed clear must never block the console.
            }
        }

        internal static void Write(Terminal terminal, string message)
        {
            try
            {
                MethodInfo direct = AccessTools.Method(typeof(Terminal), "AddString", new[] { typeof(string) });
                if (direct != null)
                {
                    direct.Invoke(terminal, new object[] { message });
                    return;
                }

                MethodInfo candidate = typeof(Terminal)
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(m => m.Name == "AddString" && m.GetParameters().Length > 0 && m.GetParameters()[0].ParameterType == typeof(string));

                if (candidate != null)
                {
                    ParameterInfo[] parameters = candidate.GetParameters();
                    object[] values = new object[parameters.Length];
                    values[0] = message;
                    for (int i = 1; i < parameters.Length; i++)
                    {
                        values[i] = parameters[i].HasDefaultValue ? parameters[i].DefaultValue : GetDefault(parameters[i].ParameterType);
                    }

                    candidate.Invoke(terminal, values);
                }
            }
            catch
            {
                // Console output is optional. The state is still logged through BepInEx.
            }
        }

        private static object GetInputObject(Terminal terminal)
        {
            string[] possibleFields = { "m_input", "m_inputField" };
            for (int i = 0; i < possibleFields.Length; i++)
            {
                FieldInfo field = AccessTools.Field(typeof(Terminal), possibleFields[i]);
                if (field != null)
                {
                    return field.GetValue(terminal);
                }
            }

            return null;
        }

        private static object GetDefault(Type type)
        {
            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }
    }

    // V0.5 synchronizes water after gameplay updates and immediately before
    // camera rendering. WaterVolume.UpdateWaterTime is static in this build,
    // so it is intentionally not used as an instance-tile hook.

    [HarmonyPatch(typeof(Terminal), "InputText")]
    internal static class TheFloodsTerminalPatch
    {
        private static bool Prefix(Terminal __instance)
        {
            try
            {
                return TheFloodsPlugin.Instance == null || !TheFloodsPlugin.Instance.TryHandleConsoleCommand(__instance);
            }
            catch
            {
                return true;
            }
        }
    }
}