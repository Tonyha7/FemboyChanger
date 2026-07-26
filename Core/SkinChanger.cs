using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FemboyChanger.Core
{
    public class SkinInfo
    {
        public int PaintKit { get; set; }
        public float Wear { get; set; } = 0.001f;
        public int Seed { get; set; } = 0;
        public int StatTrak { get; set; } = -1;

        /// <summary>Use the pre-2018 ("legacy") mesh for weapons that have two models.</summary>
        public bool LegacyModel { get; set; } = false;
    }

    public static class SkinChangerLogic
    {
        // ---- game structure constants, verified against client.dll (2026-07-22 build) ----

        /// <summary>sizeof(CEntityIdentity). The chunked entity list advances by this per slot.</summary>
        private const int EntityIdentitySize = 0x70;

        /// <summary>CGameEntitySystem -> array of 512-entry identity chunks.</summary>
        private const int EntityListChunksOffset = 0x10;

        /// <summary>CEntityIdentity::m_pInstance.</summary>
        private const int IdentityInstanceOffset = 0x00;

        /// <summary>CEntityIdentity::m_EHandle (index | serial &lt;&lt; 15).</summary>
        private const int IdentityHandleOffset = 0x10;

        /// <summary>
        /// The "regenerate every weapon whose custom materials are dirty" routine. Still a unique
        /// match in the current client.dll.
        /// </summary>
        private const string RegenerateWeaponSkinsSig = "48 83 EC ? E8 ? ? ? ? 48 85 C0 0F 84 ? ? ? ? 48 8B 10";

        /// <summary>
        /// Byte offset, inside that routine, of the 32-bit displacement in
        /// <c>cmp dword ptr [rbx+0AA8h], 0</c>. Overwriting its low word with the entity-relative
        /// offset of the attribute-list count makes the routine regenerate exactly the weapons we
        /// injected attributes into.
        /// </summary>
        private const int RegeneratePatchOffset = 0x52;

        /// <summary>Original displacement word at that site, restored when we stop.</summary>
        private const ushort RegenerateOriginalWord = 0x0AA8;

        // CT / T default knives, which get swapped for whichever knife the user picked.
        private const int DefIndexKnifeCT = 42;
        private const int DefIndexKnifeT = 59;

        // Note on legacy models: paint kits flagged use_legacy_model are authored against the
        // pre-2018 mesh, but the game switches to it entirely on its own - C_EconEntity::
        // UsesLegacyModel (sub_1807E72F0) re-derives it every frame from
        // GetPaintKitDefinition(itemView)->use_legacy_model and feeds the weapon animgraph. All we
        // have to do is keep the paint kit resolvable. Poking CModelState::m_MeshGroupMask from
        // outside only fought that per-frame recomputation, and every round it lost forced a model
        // rebuild that wiped the custom material - which is what made skins snap back to stock.

        private static bool _isRunning;
        private static Thread? _worker;

        public static Memory Mem { get; private set; } = new Memory();

        // Weapon DefIndex -> SkinInfo
        public static Dictionary<int, SkinInfo> Config = new();

        public static SkinInfo GloveConfig = new SkinInfo { PaintKit = 0 };
        public static int GloveDefIndex = 0;

        public static volatile bool ForceUpdate = false;

        private static nint _regenerateWeaponSkinsAddr;
        private static bool _regeneratePatched;
        private static int _sigScanAttempts;
        private static int _handleMismatchLogged;
        private static nint _legacyConVarAddr = -1; // -1 = not looked up yet, 0 = not found
        private static int _legacyConVarOriginal;
        private static bool _legacyConVarOverridden;

        private static string _lastState = "";

        private static readonly object LogLock = new();
        private static readonly string LogPath =
            Path.Combine(AppContext.BaseDirectory, "changer_debug.log");

        public static void Log(string message)
        {
            string line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
            Console.WriteLine(line);
            lock (LogLock)
            {
                try { File.AppendAllText(LogPath, line + Environment.NewLine); }
                catch { /* logging must never take the tool down */ }
            }
        }

        /// <summary>Logs only when the message differs from the previous state message.</summary>
        private static void LogState(string message)
        {
            if (_lastState == message) return;
            _lastState = message;
            Log(message);
        }

        public static void Start()
        {
            if (_isRunning) return;
            _isRunning = true;
            Log($"=== SkinChanger routine started ({DateTime.Now:yyyy-MM-dd HH:mm:ss}) ===");
            Log($"[Init] sizeof(CEconItemAttribute)={System.Runtime.InteropServices.Marshal.SizeOf<CEconItemAttribute>()} " +
                $"(game expects {CEconItemAttribute.Size}), sizeof(CUtlVectorHead)={System.Runtime.InteropServices.Marshal.SizeOf<CUtlVectorHead>()} (game expects 16)");
            _worker = new Thread(Routine) { IsBackground = true, Name = "SkinChanger" };
            _worker.Start();
        }

        public static void Stop()
        {
            if (!_isRunning) return;
            _isRunning = false;
            _worker?.Join(1000);
            _worker = null;

            RestoreLegacyConVar();
            RestoreRegeneratePatch();
            Mem.Dispose();
            AttributeManager.Reset();
            Log("SkinChanger routine stopped.");
        }

        private static void Routine()
        {
            while (_isRunning)
            {
                try
                {
                    Tick();
                }
                catch (Exception ex)
                {
                    Log($"[Routine] {ex.GetType().Name}: {ex.Message}");
                }
                Thread.Sleep(16);
            }
        }

        private static void Tick()
        {
            if (!Offsets.Loaded)
            {
                LogState("[Routine] Waiting for a valid offset set...");
                Thread.Sleep(500);
                return;
            }

            if (!Mem.Attach("cs2"))
            {
                LogState("[Routine] Waiting for cs2.exe (with client.dll mapped)...");
                ResetProcessState();
                Thread.Sleep(1000);
                return;
            }

            if (!EnsureRegeneratePatch()) return;
            EnsureLegacyConVar();

            nint client = Mem.ClientDll;
            nint entitySystem = Mem.Read<nint>(client + Offsets.dwEntityList);
            if (entitySystem == 0)
            {
                LogState("[Routine] Entity system not up yet.");
                return;
            }

            nint localPawn = ResolveLocalPawn(client, entitySystem);
            if (localPawn == 0)
            {
                LogState("[Routine] No local player pawn (not in a match / spectating).");
                return;
            }

            LogState("[Routine] In game - applying skins.");

            bool force = ForceUpdate;
            if (force)
                Log($"[Force] localPawn=0x{localPawn:X} entitySystem=0x{entitySystem:X} configured={Config.Count} glove={GloveDefIndex}");

            bool changed = ApplyWeapons(localPawn, entitySystem, force);
            changed |= ApplyGloves(localPawn, force);

            if (changed || force) Regenerate();

            ForceUpdate = false;
        }

        // ------------------------------------------------------------------ entity list

        /// <summary>
        /// Resolves a CHandle through CGameEntitySystem. Mirrors the game's own lookup:
        /// chunk = system[0x10 + 8 * ((handle &amp; 0x7FFF) >> 9)], identity = chunk + 0x70 * (handle &amp; 0x1FF),
        /// and the identity is only accepted when its stored handle matches.
        /// </summary>
        private static nint EntityFromHandle(nint entitySystem, int handle)
        {
            if (handle == 0 || handle == -1) return 0;

            int index = handle & 0x7FFF;
            nint chunk = Mem.Read<nint>(entitySystem + EntityListChunksOffset + 0x8 * (index >> 9));
            if (chunk == 0) return 0;

            nint identity = chunk + EntityIdentitySize * (index & 0x1FF);
            if (!Mem.Read(identity + IdentityHandleOffset, out int storedHandle)) return 0;

            if (storedHandle != handle)
            {
                if (_handleMismatchLogged < 5)
                {
                    _handleMismatchLogged++;
                    Log($"[Entity] Handle 0x{handle:X} does not match identity handle 0x{storedHandle:X} (stale entity), skipping.");
                }
                return 0;
            }

            return Mem.Read<nint>(identity + IdentityInstanceOffset);
        }

        /// <summary>
        /// Local pawn via the controller, exactly like the game does it
        /// (<c>controller = client+dwLocalPlayerController; handle = controller-&gt;m_hPawn</c>).
        /// This avoids depending on <c>dwLocalPlayerPawn</c>, whose published value has been
        /// inconsistent between offset dumps; that global is only used as a fallback.
        /// </summary>
        private static nint ResolveLocalPawn(nint client, nint entitySystem)
        {
            nint controller = Mem.Read<nint>(client + Offsets.dwLocalPlayerController);
            if (controller != 0 && Mem.Read(controller + Offsets.m_hPawn, out int pawnHandle))
            {
                nint pawn = EntityFromHandle(entitySystem, pawnHandle);
                if (pawn != 0) return pawn;
            }

            if (Offsets.dwLocalPlayerPawn != 0)
            {
                nint pawn = Mem.Read<nint>(client + Offsets.dwLocalPlayerPawn);
                if (pawn != 0 && Mem.Read<nint>(pawn) != 0) return pawn;
            }

            return 0;
        }

        // ------------------------------------------------------------------ weapons

        private static bool ApplyWeapons(nint localPawn, nint entitySystem, bool force)
        {
            nint weaponServices = Mem.Read<nint>(localPawn + Offsets.m_pWeaponServices);
            if (weaponServices == 0) return false;

            // CUtlVector<CHandle<C_BasePlayerWeapon>>: int count at +0x00, elements at +0x08.
            if (!Mem.Read(weaponServices + Offsets.m_hMyWeapons, out int weaponCount)) return false;
            nint weaponArray = Mem.Read<nint>(weaponServices + Offsets.m_hMyWeapons + 8);

            if (force)
                Log($"[Force] weaponServices=0x{weaponServices:X} count={weaponCount} array=0x{weaponArray:X}");

            if (weaponCount <= 0 || weaponCount > 64 || weaponArray == 0) return false;

            bool changed = false;
            for (int i = 0; i < weaponCount; i++)
            {
                if (!Mem.Read(weaponArray + i * 4, out int handle)) continue;
                nint weapon = EntityFromHandle(entitySystem, handle);
                if (weapon == 0) continue;

                nint itemView = weapon + Offsets.m_AttributeManager + Offsets.m_Item;
                if (!Mem.Read(itemView + Offsets.m_iItemDefinitionIndex, out ushort defIndex)) continue;

                AttributeManager.ObserveVTable(Mem, itemView);

                if (force)
                    Log($"[Force] slot {i}: handle=0x{handle:X} weapon=0x{weapon:X} defIndex={defIndex}");

                SkinInfo? skin = LookupSkin(defIndex);
                if (skin == null) continue;

                // Force re-application by clearing the "already patched" marker.
                if (force) Mem.Write(itemView + Offsets.m_iItemIDHigh, 0);

                if (!Mem.Read(itemView + Offsets.m_iItemIDHigh, out int itemIdHigh)) continue;
                if (itemIdHigh == -1) continue; // already done this life

                Mem.Write(itemView + Offsets.m_iItemIDHigh, -1);
                Mem.Write(weapon + Offsets.m_nFallbackPaintKit, skin.PaintKit);
                Mem.Write(weapon + Offsets.m_flFallbackWear, skin.Wear);
                Mem.Write(weapon + Offsets.m_nFallbackSeed, skin.Seed);
                Mem.Write(weapon + Offsets.m_nFallbackStatTrak, skin.StatTrak);

                // Drop any list we installed earlier before writing the new one. Create() refuses
                // to touch a non-empty list (stomping one the game owns would leak or crash it),
                // so without this the very first skin we install sticks forever: every later pick
                // updated the fallback fields but the stale paint-kit attribute kept winning, and
                // the weapon never changed again.
                AttributeManager.Remove(Mem, itemView);
                AttributeManager.Create(Mem, itemView, skin);

                Log($"[Skin] defIndex={defIndex} paintKit={skin.PaintKit} wear={skin.Wear} seed={skin.Seed} statTrak={skin.StatTrak} legacy={skin.LegacyModel} weapon=0x{weapon:X}");
                changed = true;
            }

            return changed;
        }

        private static SkinInfo? LookupSkin(int defIndex)
        {
            if (Config.TryGetValue(defIndex, out SkinInfo? exact)) return exact;

            // Holding a stock knife: apply whichever knife the user configured.
            if (defIndex == DefIndexKnifeCT || defIndex == DefIndexKnifeT)
            {
                foreach (KeyValuePair<int, SkinInfo> entry in Config)
                {
                    if (entry.Key >= 500 && entry.Key < 600) return entry.Value;
                }
            }

            return null;
        }

        // ------------------------------------------------------------------ gloves

        private static bool ApplyGloves(nint localPawn, bool force)
        {
            if (GloveDefIndex == 0) return false;
            if (Offsets.m_EconGloves == 0 || Offsets.m_bNeedToReApplyGloves == 0) return false;

            nint gloveItemView = localPawn + Offsets.m_EconGloves;
            if (!Mem.Read(gloveItemView + Offsets.m_iItemDefinitionIndex, out ushort currentDef)) return false;
            if (currentDef == GloveDefIndex && !force) return false;

            Log($"[Glove] defIndex={GloveDefIndex} paintKit={GloveConfig.PaintKit} wear={GloveConfig.Wear} itemView=0x{gloveItemView:X}");

            // Gloves have no entity of their own on the client - everything lives on the pawn's
            // embedded C_EconItemView, so the C_EconEntity fallback fields (m_nFallbackPaintKit &
            // friends) must NOT be written here. The previous code did, which scribbled roughly
            // 0x1680 bytes past the item view into unrelated pawn state.
            Mem.Write<byte>(gloveItemView + Offsets.m_bInitialized, 0);
            Mem.Write(gloveItemView + Offsets.m_iItemDefinitionIndex, (ushort)GloveDefIndex);
            Mem.Write(gloveItemView + Offsets.m_iItemIDHigh, -1);
            if (Offsets.m_iItemIDLow != 0) Mem.Write(gloveItemView + Offsets.m_iItemIDLow, -1);
            if (Offsets.m_iEntityQuality != 0) Mem.Write(gloveItemView + Offsets.m_iEntityQuality, 3);
            if (Offsets.m_iAccountID != 0) Mem.Write(gloveItemView + Offsets.m_iAccountID, unchecked((int)0x1337BEEF));

            // Replace any attribute list we previously installed, then install the new one.
            AttributeManager.Remove(Mem, gloveItemView);
            AttributeManager.Create(Mem, gloveItemView, GloveConfig);

            Mem.Write<byte>(gloveItemView + Offsets.m_bInitialized, 1);
            Mem.Write<byte>(localPawn + Offsets.m_bNeedToReApplyGloves, 1);
            return true;
        }

        // ------------------------------------------------------------------ regenerate

        /// <summary>
        /// Asks client.dll to rebuild the composite materials of every weapon whose attribute list
        /// we filled in (that is what the patched compare selects).
        ///
        /// The injected attributes are deliberately left in place afterwards. The game rebuilds a
        /// weapon's custom material on its own whenever the model is rebuilt, and it re-reads the
        /// paint kit through C_EconItemView::IterateAttributes every single time - so an item whose
        /// attributes we had already torn down came back as the stock skin. Leaving them installed
        /// also keeps GetPaintKitDefinition()->use_legacy_model true, which is what makes the game
        /// pick the legacy model by itself.
        /// </summary>
        private static void Regenerate()
        {
            if (_regenerateWeaponSkinsAddr == 0 || !_regeneratePatched) return;
            Mem.CallThread(_regenerateWeaponSkinsAddr);
        }

        /// <summary>
        /// Locates RegenerateWeaponSkins and repoints its first compare at the attribute-list
        /// count. Deliberately runs only once offsets are known, so we can never write a 0
        /// displacement (which made the routine fire for every weapon in the world).
        /// </summary>
        private static bool EnsureRegeneratePatch()
        {
            if (_regeneratePatched) return true;

            if (_regenerateWeaponSkinsAddr == 0)
            {
                if (_sigScanAttempts >= 5)
                {
                    LogState("[Patch] RegenerateWeaponSkins signature not found - giving up until cs2 restarts.");
                    return false;
                }

                _sigScanAttempts++;
                _regenerateWeaponSkinsAddr = Mem.SigScan(RegenerateWeaponSkinsSig);
                if (_regenerateWeaponSkinsAddr == 0)
                {
                    Log($"[Patch] RegenerateWeaponSkins signature not found (attempt {_sigScanAttempts}/5). client.dll probably changed.");
                    Thread.Sleep(1000);
                    return false;
                }

                Log($"[Patch] RegenerateWeaponSkins at 0x{_regenerateWeaponSkinsAddr:X} (client.dll+0x{_regenerateWeaponSkinsAddr - Mem.ClientDll:X})");
            }

            nint patchSite = _regenerateWeaponSkinsAddr + RegeneratePatchOffset;
            ushort expected = Mem.Read<ushort>(patchSite);
            ushort target = (ushort)Offsets.EntityAttributeCountOffset;

            if (expected == target)
            {
                _regeneratePatched = true;
                Mem.ReleaseScanCache();
                return true;
            }

            if (expected != RegenerateOriginalWord)
            {
                Log($"[Patch] Unexpected bytes at the patch site: found 0x{expected:X4}, expected 0x{RegenerateOriginalWord:X4}. " +
                    "The signature may now match a different function - refusing to patch.");
                _regenerateWeaponSkinsAddr = 0;
                _sigScanAttempts = 5;
                return false;
            }

            if (!Mem.WriteProtected(patchSite, BitConverter.GetBytes(target)))
                return false;

            _regeneratePatched = true;
            Mem.ReleaseScanCache();
            Log($"[Patch] cmp displacement 0x{expected:X4} -> 0x{target:X4} (attribute count at entity+0x{Offsets.EntityAttributeCountOffset:X})");
            return true;
        }

        /// <summary>
        /// client.dll refuses to use any legacy model when weapon_skin_force_legacy is 0 - that
        /// check runs before the paint kit is even looked at (sub_1807E72F0), so with the convar
        /// off no amount of mesh-group poking on our side can produce the right geometry. The
        /// value is logged either way, and only nudged to "per paint kit" when it is actually 0
        /// and the user has picked at least one legacy skin.
        /// </summary>
        private static void EnsureLegacyConVar()
        {
            if (_legacyConVarAddr == -1)
            {
                _legacyConVarAddr = ConVars.FindValueAddress(Mem, ConVars.WeaponSkinForceLegacy);
                if (_legacyConVarAddr > 0 && Mem.Read(_legacyConVarAddr, out int value))
                {
                    _legacyConVarOriginal = value;
                    Log($"[ConVar] {ConVars.WeaponSkinForceLegacy} = {value} (value at 0x{_legacyConVarAddr:X}); " +
                        "1 = always legacy, 0 = never, anything else = per paint kit.");
                }
                Mem.ReleaseScanCache();
            }

            if (_legacyConVarAddr <= 0 || _legacyConVarOverridden) return;
            if (!AnyLegacySkinConfigured()) return;
            if (!Mem.Read(_legacyConVarAddr, out int current) || current != 0) return;

            if (Mem.Write(_legacyConVarAddr, ConVars.ForceLegacyPerPaintKit))
            {
                _legacyConVarOverridden = true;
                Log($"[ConVar] {ConVars.WeaponSkinForceLegacy} was 0, so legacy models were disabled outright; " +
                    $"set to {ConVars.ForceLegacyPerPaintKit} (per paint kit).");
            }
        }

        private static bool AnyLegacySkinConfigured()
        {
            foreach (KeyValuePair<int, SkinInfo> entry in Config)
                if (entry.Value.LegacyModel) return true;
            return GloveDefIndex != 0 && GloveConfig.LegacyModel;
        }

        private static void RestoreLegacyConVar()
        {
            if (!_legacyConVarOverridden || _legacyConVarAddr <= 0 || !Mem.IsAttached) return;
            Mem.Write(_legacyConVarAddr, _legacyConVarOriginal);
            _legacyConVarOverridden = false;
            Log($"[ConVar] Restored {ConVars.WeaponSkinForceLegacy} to {_legacyConVarOriginal}.");
        }

        private static void RestoreRegeneratePatch()
        {
            if (!_regeneratePatched || _regenerateWeaponSkinsAddr == 0 || !Mem.IsAttached) return;
            Mem.WriteProtected(_regenerateWeaponSkinsAddr + RegeneratePatchOffset,
                               BitConverter.GetBytes(RegenerateOriginalWord));
            _regeneratePatched = false;
            Log("[Patch] Restored the original RegenerateWeaponSkins bytes.");
        }

        private static void ResetProcessState()
        {
            _regenerateWeaponSkinsAddr = 0;
            _regeneratePatched = false;
            _sigScanAttempts = 0;
            _handleMismatchLogged = 0;
            _legacyConVarAddr = -1;
            _legacyConVarOverridden = false;
            AttributeManager.Reset();
        }
    }
}
