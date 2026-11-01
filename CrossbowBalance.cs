using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Bzk.Utils;
using HarmonyLib;

namespace CrossbowBalance;

[BepInPlugin("crossbowbalance", "Crossbow Balance", "1.0.0")]
public class Plugin : BasePlugin
{
    internal static ManualLogSource L;
    internal static ConfigEntry<float> DamageMultiplier;
    internal static ConfigEntry<int> PierceCap;
    internal static ConfigEntry<bool> AmmoEnabled;
    internal static ConfigEntry<int> MaxAmmo;
    internal static ConfigEntry<bool> RefillOnDeath;
    internal static ConfigEntry<bool> ShowAmmoInHud;
    internal static ConfigEntry<bool> DimWhenUnloaded;
    internal static ConfigEntry<float> DimAlpha;

    // Current shots left. Not saved: starts full each time the game launches.
    internal static int Ammo;

    public override void Load()
    {
        L = Log;
        DamageMultiplier = Config.Bind("Damage", "DamageMultiplier", 0.5f,
            new ConfigDescription("Multiplier on the co-op crossbow's bolt damage (applies to every crossbow level).",
                new AcceptableValueRange<float>(0f, 10f)));
        PierceCap = Config.Bind("Damage", "PierceCap", 2,
            new ConfigDescription("Max enemies a piercing (upgraded) bolt can hit. 0 = unlimited (vanilla).",
                new AcceptableValueRange<int>(0, 99)));
        AmmoEnabled = Config.Bind("Ammo", "Enabled", true, "Limit crossbow shots; refills at shrines.");
        MaxAmmo = Config.Bind("Ammo", "MaxAmmo", 9,
            new ConfigDescription("Shots available after touching a shrine. The HUD counter only fits one digit, so values above 9 display badly.", new AcceptableValueRange<int>(1, 999)));
        RefillOnDeath = Config.Bind("Ammo", "RefillOnDeath", true, "Also refill when respawning after death.");
        ShowAmmoInHud = Config.Bind("Ammo", "ShowAmmoInHud", true,
            "Show remaining ammo in the crossbow's HUD counter (instead of the loaded-bolt count).");
        DimWhenUnloaded = Config.Bind("HUD", "DimWhenUnloaded", true, "Fade the crossbow's HUD counter while no bolt is loaded.");
        DimAlpha = Config.Bind("HUD", "DimAlpha", 0.35f,
            new ConfigDescription("Opacity of the counter while unloaded (1 = fully visible).", new AcceptableValueRange<float>(0f, 1f)));

        Ammo = MaxAmmo.Value;
        Harmony.CreateAndPatchAll(typeof(Patches));
        L.LogInfo($"Loaded: damage x{DamageMultiplier.Value}, ammo {(AmmoEnabled.Value ? MaxAmmo.Value.ToString() : "off")}");
    }
}

[HarmonyPatch]
internal static class Patches
{
    // Original boltDmg per crossbow model (one model per upgrade level), keyed by native pointer.
    private static readonly Dictionary<IntPtr, float> OriginalDmg = new();
    private static float lastErrorTime = -10f;
    private static bool AmmoLimited => Plugin.AmmoEnabled.Value;

    // --- Damage: scale boltDmg only for the duration of the shot, then put it back. ---
    [HarmonyPatch(typeof(HeroToolCaster), nameof(HeroToolCaster.shootBowBoltProjectile))]
    [HarmonyPrefix]
    private static void BeforeShoot(MetaCrossbowToolData bowData)
    {
        if (bowData != null) lastBowData = bowData;
        var model = bowData?.modelToolCrossbow;
        if (model == null) return;
        if (!OriginalDmg.TryGetValue(model.Pointer, out var orig))
            OriginalDmg[model.Pointer] = orig = model.boltDmg;
        model.boltDmg = orig * Plugin.DamageMultiplier.Value;
    }

    [HarmonyPatch(typeof(HeroToolCaster), nameof(HeroToolCaster.shootBowBoltProjectile))]
    [HarmonyPostfix]
    private static void AfterShoot(MetaCrossbowToolData bowData, ProjectileEnemy __result)
    {
        var model = bowData?.modelToolCrossbow;
        if (model != null && OriginalDmg.TryGetValue(model.Pointer, out var orig))
            model.boltDmg = orig;

        if (__result != null) TrackPiercingBolt(__result);

        if (__result != null && AmmoLimited && Plugin.Ammo > 0)
        {
            Plugin.Ammo--;
            hudDirty = true;
            Plugin.L.LogDebug($"Crossbow ammo: {Plugin.Ammo}/{Plugin.MaxAmmo.Value}");
        }
    }

    // --- Pierce cap ---
    // Piercing (upgraded) bolts are spawned with destroyOnTouch = false. We track only those,
    // count distinct enemies touched, and flip destroyOnTouch back on before the touch that
    // reaches the cap, so that enemy still gets hit and the bolt then ends normally.
    private sealed class BoltTrack
    {
        internal IntPtr OnTouch;          // identity check in case the projectile object gets reused
        internal float SpawnTime;
        internal readonly HashSet<IntPtr> Hit = new();
    }
    private static readonly Dictionary<IntPtr, BoltTrack> PiercingBolts = new();
    private const float BoltTrackSeconds = 6f;

    private static void TrackPiercingBolt(ProjectileEnemy bolt)
    {
        int cap = Plugin.PierceCap.Value;
        if (cap <= 0 || bolt.destroyOnTouch) return;   // cap disabled, or bolt doesn't pierce anyway
        if (cap == 1) { bolt.destroyOnTouch = true; return; }

        float now = UnityEngine.Time.time;
        // Drop stale entries (bolts that left the screen without reaching the cap).
        List<IntPtr> stale = null;
        foreach (var kv in PiercingBolts)
            if (now - kv.Value.SpawnTime > BoltTrackSeconds) (stale ??= new()).Add(kv.Key);
        if (stale != null) foreach (var k in stale) PiercingBolts.Remove(k);

        PiercingBolts[bolt.Pointer] = new BoltTrack { OnTouch = bolt.onTouch?.Pointer ?? IntPtr.Zero, SpawnTime = now };
    }

    [HarmonyPatch(typeof(ProjectileEnemy), nameof(ProjectileEnemy.onTouchTarget))]
    [HarmonyPrefix]
    private static void BeforeBoltTouch(ProjectileEnemy __instance, VectorFlash<CollisionInfo> colInfos)
    {
        if (PiercingBolts.Count == 0 || __instance == null) return;
        if (!PiercingBolts.TryGetValue(__instance.Pointer, out var track)) return;

        // Not our bolt anymore (object reused, or tracked too long): forget it, touch nothing.
        if ((__instance.onTouch?.Pointer ?? IntPtr.Zero) != track.OnTouch ||
            UnityEngine.Time.time - track.SpawnTime > BoltTrackSeconds)
        {
            PiercingBolts.Remove(__instance.Pointer);
            return;
        }

        if (colInfos != null)
            for (int i = 0; i < colInfos.length; i++)
            {
                var actor = colInfos[i]?.actorCollided;
                if (actor != null) track.Hit.Add(actor.Pointer);
            }

        if (track.Hit.Count >= Plugin.PierceCap.Value)
        {
            __instance.destroyOnTouch = true;
            PiercingBolts.Remove(__instance.Pointer);
        }
    }

    [HarmonyPatch(typeof(MetaToolDataHandler), nameof(MetaToolDataHandler.resetToolStatesOnRoomChange))]
    [HarmonyPostfix]
    private static void ClearBoltsOnRoomChange() => PiercingBolts.Clear();

    // --- HUD: the co-op tool icon's "xN" counter (ViewToolNum, owned by UI_Ingame_2PCoop). ---
    // Set when ammo changes; the next HeroToolCaster.update asks the game to redraw its tool UI.
    private static bool hudDirty;

    [HarmonyPatch(typeof(ViewToolNum), nameof(ViewToolNum.refresh))]
    [HarmonyPostfix]
    private static void AfterToolNumRefresh(ViewToolNum __instance)
    {
        var tool = HeroToolCaster.currentEquippedTool;
        bool isCrossbow = tool != null && tool.typeEnumId == EEquipTool.CROSSBOW;
        var txt = __instance.potionQtyTxt;

        if (isCrossbow && AmmoLimited && Plugin.ShowAmmoInHud.Value && txt != null)
            txt.text = Plugin.Ammo.ToString();

        // Dim "xN" while no bolt is loaded; undo it when that's no longer the case
        // (including after swapping to another tool, so its counter isn't left dimmed).
        bool dim = false;
        if (isCrossbow && Plugin.DimWhenUnloaded.Value)
        {
            var bowData = GetCrossbowData();
            dim = bowData != null && bowData.getToolQteInLoadedState() == 0;
        }
        if (dim || counterDimmed)
        {
            float a = dim ? Plugin.DimAlpha.Value : 1f;
            if (txt != null) txt.alpha = a;
            if (__instance.mcX != null) __instance.mcX.alpha = a;
            counterDimmed = dim;
        }
    }
    private static bool counterDimmed;

    // --- Ammo: when empty, swallow the sidekick's tool input while the crossbow is equipped. ---
    [HarmonyPatch(typeof(HeroToolCaster), nameof(HeroToolCaster.tryHandlingToolPrimaryInput))]
    [HarmonyPrefix]
    private static bool BeforePrimaryInput()
    {
        if (!AmmoLimited || Plugin.Ammo > 0) return true;
        var tool = HeroToolCaster.currentEquippedTool;
        if (tool == null || tool.typeEnumId != EEquipTool.CROSSBOW) return true;

        ShakeError();
        return false;
    }

    // --- No phantom bolts: never load more bolts than there is ammo left. ---
    [HarmonyPatch(typeof(HeroToolCaster), nameof(HeroToolCaster.doCrossbowReload))]
    [HarmonyPrefix]
    private static bool BeforeReload(MetaCrossbowToolData bowData)
    {
        if (!AmmoLimited || bowData == null) return true;
        lastBowData = bowData;
        if (bowData.getToolQteInLoadedState() < Plugin.Ammo) return true;
        ShakeError();
        return false;
    }

    private static void ShakeError()
    {
        // Rate-limited in case the caller runs every frame rather than only on button press.
        float now = UnityEngine.Time.unscaledTime;
        if (now - lastErrorTime > 0.75f)
        {
            lastErrorTime = now;
            HeroToolCaster.playToolErrorUIAnim();
        }
    }

    // The game reloads reloadable tools when it resets their state (room change, victory, ...).
    // Right after any such reset, unload crossbow bolts beyond the ammo count.
    // NOTE: hooked on MetaToolDataHandler, not on MetaReloadableToolData's tiny reset methods:
    // patching several of those crashed the game on load (likely merged native code).
    [HarmonyPatch(typeof(MetaToolDataHandler), nameof(MetaToolDataHandler.resetToolStatesOnRoomChange))]
    [HarmonyPostfix] private static void AfterResetRoom() => ClampAfter("room change");

    [HarmonyPatch(typeof(MetaToolDataHandler), nameof(MetaToolDataHandler.resetToolStatesOnVictory))]
    [HarmonyPostfix] private static void AfterResetVictory() => ClampAfter("victory");

    [HarmonyPatch(typeof(MetaToolDataHandler), nameof(MetaToolDataHandler.resetToolStatesOnUpgrade))]
    [HarmonyPostfix] private static void AfterResetUpgrade() => ClampAfter("upgrade");

    private static void ClampAfter(string why)
    {
        if (!AmmoLimited) return;
        var bowData = GetCrossbowData();
        if (bowData != null && Clamp(bowData))
            Plugin.L.LogDebug($"Unloaded extra crossbow bolts after {why} (ammo {Plugin.Ammo})");
    }

    // Backup for any other path that loads bolts.
    [HarmonyPatch(typeof(HeroToolCaster), nameof(HeroToolCaster.update))]
    [HarmonyPostfix]
    private static void AfterCasterUpdate()
    {
        if (hudDirty)
        {
            hudDirty = false;
            try { HeroToolCaster.refreshUI(); }
            catch (Exception e) { Plugin.L.LogWarning($"refreshUI failed: {e.Message}"); }
        }
        if (!AmmoLimited || Plugin.Ammo >= Plugin.MaxAmmo.Value) return;
        var bowData = GetCrossbowData();
        if (bowData != null && Clamp(bowData))
            Plugin.L.LogDebug($"Unloaded extra crossbow bolts in update (ammo {Plugin.Ammo})");
    }

    /// Unloads bolts beyond the ammo count; returns true if anything changed.
    private static bool Clamp(MetaReloadableToolData data)
    {
        bool changed = false;
        int guard = 16;
        while (data.getToolQteInLoadedState() > Plugin.Ammo && guard-- > 0)
        {
            var bolt = data.getFirstLoadedTool();
            if (bolt == null) break;
            bolt.setStatus(ReloadableToolStatus.EStatus.UNLOADED);
            changed = true;
        }
        if (changed)
        {
            try { HeroToolCaster.refreshUI(); }
            catch (Exception e) { Plugin.L.LogWarning($"refreshUI failed: {e.Message}"); }
        }
        return changed;
    }

    private static MetaCrossbowToolData lastBowData;
    private static bool lookupFailedLogged;
    private static MetaCrossbowToolData GetCrossbowData()
    {
        try
        {
            var found = HeroToolCaster.toolsMetaData?.getFirstOwnedMetaToolData<MetaCrossbowToolData>(EEquipTool.CROSSBOW);
            if (found != null) return found;
        }
        catch (Exception e)
        {
            if (!lookupFailedLogged) { lookupFailedLogged = true; Plugin.L.LogWarning($"Crossbow data lookup failed: {e.Message}"); }
        }
        return lastBowData; // last one seen when shooting/reloading
    }

    // --- Make shrines activate when only crossbow ammo is missing. ---
    [HarmonyPatch(typeof(MetaToolDataHandler), nameof(MetaToolDataHandler.needsShrineRefill))]
    [HarmonyPostfix]
    private static void NeedsShrineRefill(ref bool __result)
    {
        if (AmmoLimited && Plugin.Ammo < Plugin.MaxAmmo.Value) __result = true;
    }

    // --- Refill ---
    [HarmonyPatch(typeof(MetaToolDataHandler), nameof(MetaToolDataHandler.resetToolStatesOnTouchShrine))]
    [HarmonyPostfix]
    private static void OnShrine() => Refill("shrine");

    [HarmonyPatch(typeof(MetaToolDataHandler), nameof(MetaToolDataHandler.resetToolStatesOnResetFromDeath))]
    [HarmonyPostfix]
    private static void OnDeathReset()
    {
        if (Plugin.RefillOnDeath.Value) Refill("death");
        else ClampAfter("death");
    }

    private static void Refill(string why)
    {
        Plugin.Ammo = Plugin.MaxAmmo.Value;
        hudDirty = true;
        Plugin.L.LogDebug($"Crossbow ammo refilled ({why}): {Plugin.Ammo}");

        // Bolts we held back while empty won't necessarily reload on their own,
        // so use the game's own tool reset to put them back in the loaded state.
        var bowData = GetCrossbowData();
        if (bowData != null && bowData.hasUnloaded())
        {
            bowData.resetState();
            Plugin.L.LogDebug($"Crossbow reloaded after refill: {bowData.getToolQteInLoadedState()} loaded");
        }
    }
}
