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
    }

    public static class SkinChangerLogic
    {
        private static bool _isRunning = false;
        public static Memory Mem { get; private set; } = new Memory();
        
        // Weapon DefIndex -> SkinInfo
        public static Dictionary<int, SkinInfo> Config = new Dictionary<int, SkinInfo>();
        
        public static SkinInfo GloveConfig = new SkinInfo { PaintKit = 0 };
        public static int GloveDefIndex = 0;

        public static bool ForceUpdate = false;
        private static nint _regenerateWeaponSkinsAddr = 0;

        public static void Log(string message)
        {
            
            return;
            
            string line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
            Console.WriteLine(line);
            try
            {
                File.AppendAllText("changer_debug.log", line + "\n");
            }
            catch { }
        }

        public static void Start()
        {
            if (_isRunning) return;
            _isRunning = true;
            Log("SkinChanger routine started.");
            Task.Run(Routine);
        }

        public static void Stop()
        {
            _isRunning = false;
            Mem.Dispose();
            Log("SkinChanger routine stopped.");
        }

        private static void Routine()
        {
            int tick = 0;
            while (_isRunning)
            {
                Thread.Sleep(5);
                tick++;

                if (!Mem.Attach("cs2"))
                {
                    if (tick % 200 == 0) Log("Waiting for cs2.exe...");
                    Thread.Sleep(1000);
                    _regenerateWeaponSkinsAddr = 0;
                    continue;
                }

                if (_regenerateWeaponSkinsAddr == 0)
                {
                    _regenerateWeaponSkinsAddr = Mem.SigScan("48 83 EC ? E8 ? ? ? ? 48 85 C0 0F 84 ? ? ? ? 48 8B 10");
                    if (_regenerateWeaponSkinsAddr != 0)
                    {
                        Log($"Found RegenerateWeaponSkins at: {_regenerateWeaponSkinsAddr:X}");
                        // Apply the patch C++ does
                        Mem.Write<ushort>(_regenerateWeaponSkinsAddr + 0x52, (ushort)(Offsets.m_AttributeManager + Offsets.m_Item + Offsets.m_AttributeList + Offsets.m_Attributes));
                    }
                    else
                    {
                        Log("Failed to find RegenerateWeaponSkins signature!");
                    }
                }

                try
                {
                    IntPtr client = Mem.ClientDll;
                    nint localPlayer = Mem.Read<nint>(client + Offsets.dwLocalPlayerPawn);
                    if (localPlayer == 0) continue;

                    nint weaponServices = Mem.Read<nint>(localPlayer + Offsets.m_pWeaponServices);
                    if (weaponServices == 0) continue;

                    long weaponCount = Mem.Read<long>(weaponServices + Offsets.m_hMyWeapons);
                    nint hWeapons = Mem.Read<nint>(weaponServices + Offsets.m_hMyWeapons + 8);
                    
                    nint entityListBase = Mem.Read<nint>(client + Offsets.dwEntityList);

                    bool shouldUpdate = false;
                    List<nint> updatedWeapons = new List<nint>();

                    if (ForceUpdate)
                    {
                        Log($"Force update triggered. LocalPlayer: {localPlayer:X}, ws: {weaponServices:X}, count: {weaponCount}, hWeapons: {hWeapons:X}, entityListBase: {entityListBase:X}");
                    }

                    if (weaponCount > 0 && weaponCount <= 64 && hWeapons != 0)
                    {
                        for (int i = 0; i < weaponCount; i++)
                        {
                            int weaponHandle = Mem.Read<int>(hWeapons + (i * 0x4));
                            if (ForceUpdate) Log($"Weapon {i} handle: {weaponHandle:X}");
                            if (weaponHandle == 0 || weaponHandle == -1) continue;

                            nint listEntry = Mem.Read<nint>(entityListBase + 0x8 * ((weaponHandle & 0x7FFF) >> 9) + 0x10);
                            if (ForceUpdate) Log($"Weapon {i} listEntry: {listEntry:X}");
                            if (listEntry == 0) continue;
                            
                            nint weapon = Mem.Read<nint>(listEntry + 0x70 * (weaponHandle & 0x1FF)); // IMPORTANT: it's 0x70 in the new entity list, not 0x78
                            if (ForceUpdate) Log($"Weapon {i} ptr: {weapon:X}");
                            if (weapon == 0) continue;

                            nint item = weapon + Offsets.m_AttributeManager + Offsets.m_Item;
                            short defIndex = Mem.Read<short>(item + Offsets.m_iItemDefinitionIndex);
                            if (ForceUpdate) Log($"Weapon {i} defIndex: {defIndex}");

                              int lookupIndex = defIndex;
                              // If holding CT or T default knife, and we selected a custom knife skin
                              if (defIndex == 42 || defIndex == 59)
                              {
                                  foreach (int key in Config.Keys)
                                  {
                                      // Knife IDs are usually 500~599
                                      if (key >= 500 && key < 600)
                                      {
                                          lookupIndex = key;
                                          break;
                                      }
                                  }
                              }

                              if (Config.TryGetValue(lookupIndex, out SkinInfo skin))
                            {
                                if (ForceUpdate)
                                {
                                    Log($"Force updating weapon defIndex: {defIndex}, setting ItemIDHigh to 0");
                                    Mem.Write<int>(item + Offsets.m_iItemIDHigh, 0);
                                }

                                int itemIdHigh = Mem.Read<int>(item + Offsets.m_iItemIDHigh);
                                if (itemIdHigh == -1) continue;
                                
                                Log($"Applying skin {skin.PaintKit} to weapon {defIndex} (Weapon Ptr: {weapon:X})");
                                Mem.Write<int>(item + Offsets.m_iItemIDHigh, -1);
                                Mem.Write<int>(weapon + Offsets.m_nFallbackPaintKit, skin.PaintKit);
                                Mem.Write<float>(weapon + Offsets.m_flFallbackWear, skin.Wear);
                                Mem.Write<int>(weapon + Offsets.m_nFallbackSeed, skin.Seed);
                                Mem.Write<int>(weapon + Offsets.m_nFallbackStatTrak, skin.StatTrak);

                                  AttributeManager.Create(Mem, item, skin);

                                  // Old model mask
                                  nint gameSceneNode = Mem.Read<nint>(weapon + Offsets.m_pGameSceneNode);
                                  if (gameSceneNode != 0)
                                  {
                                      ulong mask = 1; // 1 for new models, 2 for old models
                                      // USP-S (61), M4A1-S (60), M4A4 (16), AWP (9), SCAR-20 (38), SSG 08 (40)
                                      if (defIndex == 61 || defIndex == 60 || defIndex == 16 || defIndex == 9 || defIndex == 38 || defIndex == 40)
                                      {
                                          mask = 1;
                                      }
                                      Mem.Write<ulong>(gameSceneNode + Offsets.m_modelState + Offsets.m_MeshGroupMask, mask);
                                  }

                                  shouldUpdate = true;
                                  updatedWeapons.Add(weapon);
                            }
                        }
                    }

                    // Gloves logic (basic)
                    if (GloveDefIndex != 0)
                    {
                        nint econGloves = localPlayer + Offsets.m_EconGloves;
                        short currentDef = Mem.Read<short>(econGloves + Offsets.m_iItemDefinitionIndex);
                        if (currentDef != GloveDefIndex || ForceUpdate)
                        {
                            Log($"Applying glove defIndex: {GloveDefIndex}, paintKit: {GloveConfig.PaintKit}");
                            Mem.Write<bool>(econGloves + Offsets.m_bInitialized, false);
                            Mem.Write<short>(econGloves + Offsets.m_iItemDefinitionIndex, (short)GloveDefIndex);
                            Mem.Write<int>(econGloves + Offsets.m_iItemIDHigh, -1);
                            Mem.Write<int>(econGloves + Offsets.m_nFallbackPaintKit, GloveConfig.PaintKit);
                            Mem.Write<float>(econGloves + Offsets.m_flFallbackWear, GloveConfig.Wear);
                            Mem.Write<int>(econGloves + Offsets.m_iEntityQuality, 3);
                            Mem.Write<int>(econGloves + Offsets.m_iAccountID, 12345);

                            SkinInfo gloveSkin = new SkinInfo { PaintKit = GloveConfig.PaintKit, Wear = GloveConfig.Wear };
                            AttributeManager.Create(Mem, econGloves, gloveSkin);

                            Mem.Write<bool>(econGloves + Offsets.m_bInitialized, true);
                            Mem.Write<bool>(localPlayer + Offsets.m_bNeedToReApplyGloves, true);
                            
                            shouldUpdate = true;
                        }
                    }

                    if ((shouldUpdate || ForceUpdate) && _regenerateWeaponSkinsAddr != 0)
                    {
                        Mem.CallThread(_regenerateWeaponSkinsAddr);
                        foreach (var wp in updatedWeapons)
                        {
                            nint it = wp + Offsets.m_AttributeManager + Offsets.m_Item;
                            if (Mem.Read<int>(wp + Offsets.m_nFallbackPaintKit) != -1)
                            {
                                Mem.Write<int>(wp + Offsets.m_nFallbackPaintKit, -1);
                                AttributeManager.Remove(Mem, it);
                            }
                        }
                    }

                    ForceUpdate = false;
                }
                catch (Exception ex)
                {
                    if (tick % 200 == 0) Log($"Error in routine: {ex.Message}");
                }
            }
        }
    }
}