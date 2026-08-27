using System.Collections.Generic;
using System.Reflection;
using FireFront.Config;
using FireFront.Fire;
using FireFront.Utils;
using HarmonyLib;
using UnityEngine;

namespace FireFront.Patches
{
    /// <summary>
    /// The Dousing Bomb: a craftable thrown item that puts fire out where it
    /// lands — ground fire and burning objects both, within DousingBombRadius.
    ///
    /// Built by cloning vanilla's ooze bomb (BombOoze) at ObjectDB setup, so
    /// throw handling, physics, stacking and consumption are all vanilla's
    /// own. The only custom behavior is at impact, via a Projectile.OnHit
    /// prefix keyed on the thrown item's name: suppress the ooze cloud the
    /// donor prefab would spawn, then route the hit point into the same
    /// FireFront_ExtinguishRequest flow the extinguish key already uses —
    /// HandleExtinguishRequest accepts ZDOID.None plus an arbitrary position,
    /// so this adds no new RPC and no version-skew surface.
    ///
    /// No Jotunn: registration is a direct ObjectDB/ZNetScene insert under a
    /// stable prefab name. Every peer runs this same code, so the name-hash
    /// resolves identically everywhere — the same determinism vanilla's own
    /// prefab registry relies on.
    /// </summary>
    public static class DousingBomb
    {
        public const string PrefabName = "FireFront_DousingBomb";
        public const string DisplayName = "Dousing bomb";
        private const string RecipeName = "Recipe_FireFrontDousingBomb";

        private static GameObject _prefab;
        private static GameObject _container; // inactive holder so the clone never runs in-scene
        private static bool _failureLogged;

        // ObjectDB.m_itemByHash is PRIVATE in the real assembly — direct access
        // compiled against the publicized DLL and threw FieldAccessException at
        // runtime (confirmed live, client main menu, 0.17.5). m_items proved
        // public in the same trace (its Add executed before the throw), but
        // m_recipes is unproven, so both go through reflection.
        private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo ItemByHashField = typeof(ObjectDB).GetField("m_itemByHash", AnyInstance);
        private static readonly FieldInfo ItemsField = typeof(ObjectDB).GetField("m_items", AnyInstance);
        private static readonly FieldInfo RecipesField = typeof(ObjectDB).GetField("m_recipes", AnyInstance);

        /// <summary>Clone the donor once. Null (with one log) if it can't be built.</summary>
        public static GameObject EnsureCreated(ObjectDB db)
        {
            if (_prefab != null) return _prefab;
            if (db == null) return null;

            GameObject donor = db.GetItemPrefab("BombOoze");
            if (donor == null)
            {
                if (!_failureLogged)
                {
                    _failureLogged = true;
                    FireLogger.Warn("[DOUSING] donor prefab 'BombOoze' not found in ObjectDB — Dousing Bomb unavailable.");
                }
                return null;
            }

            if (_container == null)
            {
                _container = new GameObject("FireFront_Prefabs");
                _container.SetActive(false);
                Object.DontDestroyOnLoad(_container);
            }

            GameObject clone = Object.Instantiate(donor, _container.transform);
            clone.name = PrefabName;

            ItemDrop drop = clone.GetComponent<ItemDrop>();
            if (drop == null)
            {
                if (!_failureLogged)
                {
                    _failureLogged = true;
                    FireLogger.Warn("[DOUSING] BombOoze clone has no ItemDrop — Dousing Bomb unavailable.");
                }
                Object.Destroy(clone);
                return null;
            }

            // m_shared is a REFERENCE shared with the donor — without this copy,
            // renaming here renames every ooze bomb in the game.
            drop.m_itemData.m_shared = CloneShared(drop.m_itemData.m_shared);
            drop.m_itemData.m_shared.m_name = DisplayName;
            drop.m_itemData.m_shared.m_description =
                "Smothers fire where it lands — grass fire and burning structures or trees alike.";

            _prefab = clone;
            FireLogger.Info($"[DOUSING] '{PrefabName}' created from BombOoze.");
            return _prefab;
        }

        private static ItemDrop.ItemData.SharedData CloneShared(ItemDrop.ItemData.SharedData source)
        {
            var copy = new ItemDrop.ItemData.SharedData();
            FieldInfo[] fields = typeof(ItemDrop.ItemData.SharedData)
                .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < fields.Length; i++)
                fields[i].SetValue(copy, fields[i].GetValue(source));
            return copy;
        }

        /// <summary>
        /// Idempotent: safe from both ObjectDB.Awake and CopyOtherDB, in any
        /// order relative to ZNetScene.Awake (whichever runs last still sees
        /// the item registered everywhere it needs to be).
        /// </summary>
        public static void RegisterIntoObjectDB(ObjectDB db)
        {
            GameObject prefab = EnsureCreated(db);
            if (prefab == null) return;

            var itemByHash = ItemByHashField?.GetValue(db) as Dictionary<int, GameObject>;
            var items = ItemsField?.GetValue(db) as List<GameObject>;
            var recipes = RecipesField?.GetValue(db) as List<Recipe>;
            if (itemByHash == null || items == null || recipes == null)
            {
                if (!_failureLogged)
                {
                    _failureLogged = true;
                    FireLogger.Warn("[DOUSING] ObjectDB registries unreadable via reflection " +
                                    $"(itemByHash {itemByHash == null}, items {items == null}, recipes {recipes == null}) " +
                                    "— Dousing Bomb unavailable.");
                }
                return;
            }

            int hash = prefab.name.GetStableHashCode();
            if (!itemByHash.ContainsKey(hash))
            {
                items.Add(prefab);
                itemByHash.Add(hash, prefab);
            }

            bool haveRecipe = false;
            for (int i = 0; i < recipes.Count; i++)
            {
                if (recipes[i] != null && recipes[i].name == RecipeName) { haveRecipe = true; break; }
            }
            if (!haveRecipe)
            {
                GameObject resin = db.GetItemPrefab("Resin");
                GameObject scraps = db.GetItemPrefab("LeatherScraps");
                if (resin != null && scraps != null)
                {
                    Recipe recipe = ScriptableObject.CreateInstance<Recipe>();
                    recipe.name = RecipeName;
                    recipe.m_item = prefab.GetComponent<ItemDrop>();
                    recipe.m_amount = 3;
                    recipe.m_enabled = true;
                    // Hand-craftable, cheap, early-game on purpose: this exists so
                    // testers can fight the fire, not as an economy item (yet).
                    recipe.m_resources = new[]
                    {
                        new Piece.Requirement { m_resItem = resin.GetComponent<ItemDrop>(), m_amount = 3 },
                        new Piece.Requirement { m_resItem = scraps.GetComponent<ItemDrop>(), m_amount = 2 },
                    };
                    recipes.Add(recipe);
                    FireLogger.Info("[DOUSING] recipe registered (3x Resin + 2x Leather scraps -> 3 bombs, hand-craftable).");
                }
                else
                {
                    FireLogger.Warn("[DOUSING] recipe ingredients missing from ObjectDB — item registered without a recipe.");
                }
            }

            // Cover the "ObjectDB ready after ZNetScene.Awake already ran" order.
            if (ZNetScene.instance != null)
                ValheimBridge.RegisterPrefabWithZNetScene(ZNetScene.instance, prefab);
        }
    }

    // Every registration postfix swallows its own failure with one warning:
    // an exception escaping a postfix surfaces as a raw Unity error inside
    // vanilla's own setup path (seen live in 0.17.5), and a broken tester
    // item must degrade to "item missing", never to "menu spams errors".
    [HarmonyPatch(typeof(ObjectDB), "Awake")]
    public static class ObjectDBAwakeDousingPatch
    {
        private static bool _thrown;

        private static void Postfix(ObjectDB __instance)
        {
            try { DousingBomb.RegisterIntoObjectDB(__instance); }
            catch (System.Exception ex)
            {
                if (_thrown) return;
                _thrown = true;
                FireLogger.Warn($"[DOUSING] registration threw at ObjectDB.Awake: {ex.Message} — Dousing Bomb unavailable.");
            }
        }
    }

    [HarmonyPatch(typeof(ObjectDB), "CopyOtherDB")]
    public static class ObjectDBCopyDousingPatch
    {
        private static bool _thrown;

        private static void Postfix(ObjectDB __instance)
        {
            try { DousingBomb.RegisterIntoObjectDB(__instance); }
            catch (System.Exception ex)
            {
                if (_thrown) return;
                _thrown = true;
                FireLogger.Warn($"[DOUSING] registration threw at ObjectDB.CopyOtherDB: {ex.Message} — Dousing Bomb unavailable.");
            }
        }
    }

    [HarmonyPatch(typeof(ZNetScene), "Awake")]
    public static class ZNetSceneAwakeDousingPatch
    {
        private static bool _thrown;

        private static void Postfix(ZNetScene __instance)
        {
            try
            {
                GameObject prefab = DousingBomb.EnsureCreated(ObjectDB.instance);
                if (prefab != null) ValheimBridge.RegisterPrefabWithZNetScene(__instance, prefab);
            }
            catch (System.Exception ex)
            {
                if (_thrown) return;
                _thrown = true;
                FireLogger.Warn($"[DOUSING] registration threw at ZNetScene.Awake: {ex.Message} — Dousing Bomb unavailable.");
            }
        }
    }

    /// <summary>
    /// Impact behavior. OnHit runs on the thrower's peer only (the projectile
    /// owner), so exactly one extinguish request per bomb; a bounce touching
    /// twice just repeats an idempotent clear. Fields read by reflection per
    /// the bridge's publicized-DLL rule.
    /// </summary>
    [HarmonyPatch(typeof(Projectile), "OnHit")]
    public static class ProjectileOnHitDousingPatch
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo WeaponField = typeof(Projectile).GetField("m_weapon", Any);
        private static readonly FieldInfo SpawnOnHitField = typeof(Projectile).GetField("m_spawnOnHit", Any);

        private static void Prefix(Projectile __instance, Vector3 hitPoint)
        {
            var weapon = WeaponField?.GetValue(__instance) as ItemDrop.ItemData;
            if (weapon?.m_shared == null || weapon.m_shared.m_name != DousingBomb.DisplayName) return;

            // The donor's ooze cloud must not spawn from OUR bomb.
            SpawnOnHitField?.SetValue(__instance, null);

            float radius = FireConfig.DousingBombRadius.Value;
            if (ValheimBridge.IsServer())
            {
                FireManager.Instance?.ExtinguishAt(hitPoint, radius);
            }
            else
            {
                ValheimBridge.SendExtinguishRequestToServer(ZDOID.None, hitPoint, radius);
            }
            FireLogger.Debug($"[DOUSING] bomb hit at ({hitPoint.x:F1},{hitPoint.z:F1}) — extinguishing r={radius:F1}m.");
        }
    }
}
