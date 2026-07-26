using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace FemboyChanger.Core
{
    public static class Offsets
    {
        // ---- client.dll globals (from offsets.json) ----
        public static nint dwEntityList;
        public static nint dwLocalPlayerPawn;
        public static nint dwLocalPlayerController;

        // ---- schema offsets (from client_dll.json) ----
        public static nint m_hPawn;                 // CBasePlayerController  -> CHandle<C_BasePlayerPawn>
        public static nint m_pWeaponServices;       // C_BasePlayerPawn
        public static nint m_hMyWeapons;            // CPlayer_WeaponServices -> CUtlVector<CHandle>
        public static nint m_AttributeManager;      // C_EconEntity           -> C_AttributeContainer
        public static nint m_Item;                  // C_AttributeContainer   -> C_EconItemView
        public static nint m_iItemDefinitionIndex;  // C_EconItemView (uint16)
        public static nint m_nFallbackPaintKit;     // C_EconEntity
        public static nint m_nFallbackSeed;         // C_EconEntity
        public static nint m_flFallbackWear;        // C_EconEntity
        public static nint m_nFallbackStatTrak;     // C_EconEntity
        public static nint m_iEntityQuality;        // C_EconItemView
        public static nint m_iItemIDHigh;           // C_EconItemView
        public static nint m_iItemIDLow;            // C_EconItemView
        public static nint m_iAccountID;            // C_EconItemView
        public static nint m_OriginalOwnerXuidLow;  // C_EconEntity
        public static nint m_hViewmodelAttachment;  // C_EconEntity -> CHandle<C_BaseViewModel>, the entity actually drawn in first person
        public static nint m_EconGloves;            // C_CSPlayerPawn -> C_EconItemView
        public static nint m_bNeedToReApplyGloves;  // C_CSPlayerPawn
        public static nint m_bInitialized;          // C_EconItemView
        public static nint m_pInventoryServices;    // CCSPlayerController
        public static nint m_unMusicID;             // CCSPlayerController_InventoryServices
        public static nint m_AttributeList;         // C_EconItemView -> CAttributeList
        public static nint m_Attributes;            // CAttributeList -> CUtlVectorEmbeddedNetworkVar<CEconItemAttribute>
        public static nint m_MeshGroupMask;         // CModelState
        public static nint m_pGameSceneNode;        // C_BaseEntity -> CSkeletonInstance
        public static nint m_modelState;            // CSkeletonInstance -> CModelState

        /// <summary>
        /// Absolute offset, from the start of a C_EconEntity, of the CUtlVector element-count
        /// that lives in m_AttributeManager.m_Item.m_AttributeList.m_Attributes.
        /// This is the value patched into the RegenerateWeaponSkins compare instruction.
        /// </summary>
        public static nint EntityAttributeCountOffset =>
            m_AttributeManager + m_Item + m_AttributeList + m_Attributes;

        /// <summary>True once every offset needed by the skin changer resolved to a non-zero value.</summary>
        public static bool Loaded { get; private set; }

        /// <summary>Where the data came from, for the log.</summary>
        public static string Source { get; private set; } = "none";

        private const string OffsetsUrl = "https://ob.tonyha7.com/offsets.json";
        private const string ClientDllUrl = "https://ob.tonyha7.com/client_dll.json";

        public static async Task LoadOffsetsAsync()
        {
            Loaded = false;

            JObject? offsetsData = await LoadJsonAsync(OffsetsUrl, "offsets.json");
            JObject? cdllData = await LoadJsonAsync(ClientDllUrl, "client_dll.json");

            if (offsetsData == null || cdllData == null)
            {
                SkinChangerLogic.Log("[Offsets] FATAL: could not obtain offsets.json / client_dll.json (no network and no local copy).");
                return;
            }

            try
            {
                JToken? client = offsetsData["client.dll"];
                dwEntityList = ReadGlobal(client, "dwEntityList");
                dwLocalPlayerController = ReadGlobal(client, "dwLocalPlayerController");
                dwLocalPlayerPawn = ReadGlobal(client, "dwLocalPlayerPawn");

                JToken? classes = cdllData["client.dll"]?["classes"];

                m_hPawn = ParseOffset(classes, "CBasePlayerController", "m_hPawn");
                m_pInventoryServices = ParseOffset(classes, "CCSPlayerController", "m_pInventoryServices");
                m_unMusicID = ParseOffset(classes, "CCSPlayerController_InventoryServices", "m_unMusicID");

                m_pWeaponServices = ParseOffset(classes, "C_BasePlayerPawn", "m_pWeaponServices");
                m_hMyWeapons = ParseOffset(classes, "CPlayer_WeaponServices", "m_hMyWeapons");

                m_EconGloves = ParseOffset(classes, "C_CSPlayerPawn", "m_EconGloves");
                m_bNeedToReApplyGloves = ParseOffset(classes, "C_CSPlayerPawn", "m_bNeedToReApplyGloves");

                m_bInitialized = ParseOffset(classes, "C_EconItemView", "m_bInitialized");
                m_iEntityQuality = ParseOffset(classes, "C_EconItemView", "m_iEntityQuality");
                m_iItemDefinitionIndex = ParseOffset(classes, "C_EconItemView", "m_iItemDefinitionIndex");
                m_iAccountID = ParseOffset(classes, "C_EconItemView", "m_iAccountID");
                m_iItemIDHigh = ParseOffset(classes, "C_EconItemView", "m_iItemIDHigh");
                m_iItemIDLow = ParseOffset(classes, "C_EconItemView", "m_iItemIDLow");
                m_AttributeList = ParseOffset(classes, "C_EconItemView", "m_AttributeList");

                m_nFallbackPaintKit = ParseOffset(classes, "C_EconEntity", "m_nFallbackPaintKit");
                m_nFallbackStatTrak = ParseOffset(classes, "C_EconEntity", "m_nFallbackStatTrak");
                m_flFallbackWear = ParseOffset(classes, "C_EconEntity", "m_flFallbackWear");
                m_nFallbackSeed = ParseOffset(classes, "C_EconEntity", "m_nFallbackSeed");
                m_OriginalOwnerXuidLow = ParseOffset(classes, "C_EconEntity", "m_OriginalOwnerXuidLow");
                m_AttributeManager = ParseOffset(classes, "C_EconEntity", "m_AttributeManager");
                m_hViewmodelAttachment = ParseOffset(classes, "C_EconEntity", "m_hViewmodelAttachment");

                m_Item = ParseOffset(classes, "C_AttributeContainer", "m_Item");
                m_Attributes = ParseOffset(classes, "CAttributeList", "m_Attributes");

                m_pGameSceneNode = ParseOffset(classes, "C_BaseEntity", "m_pGameSceneNode");
                m_modelState = ParseOffset(classes, "CSkeletonInstance", "m_modelState");
                m_MeshGroupMask = ParseOffset(classes, "CModelState", "m_MeshGroupMask");

                Loaded = Validate();
                DumpToLog();
            }
            catch (Exception ex)
            {
                SkinChangerLogic.Log("[Offsets] Failed to parse offsets: " + ex);
            }
        }

        /// <summary>
        /// Fetches <paramref name="url"/>; on any failure falls back to a file of the same
        /// name next to the executable (or in the working directory). Keeping a local copy
        /// means a dead/stale offset feed no longer bricks the whole tool.
        /// </summary>
        private static async Task<JObject?> LoadJsonAsync(string url, string fileName)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                string json = await http.GetStringAsync(url);
                var obj = JObject.Parse(json);
                Source = "remote";
                // Keep a local copy so the next launch works offline / if the feed goes down.
                TryCache(fileName, json);
                return obj;
            }
            catch (Exception ex)
            {
                SkinChangerLogic.Log($"[Offsets] Download of {fileName} failed ({ex.Message}); trying local copy.");
            }

            foreach (string dir in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
            {
                try
                {
                    string path = Path.Combine(dir, fileName);
                    if (!File.Exists(path)) continue;
                    var obj = JObject.Parse(File.ReadAllText(path));
                    Source = "local:" + path;
                    SkinChangerLogic.Log($"[Offsets] Using local {path} (last written {File.GetLastWriteTime(path):yyyy-MM-dd HH:mm}).");
                    return obj;
                }
                catch (Exception ex)
                {
                    SkinChangerLogic.Log($"[Offsets] Local {fileName} in {dir} unusable: {ex.Message}");
                }
            }
            return null;
        }

        private static void TryCache(string fileName, string json)
        {
            try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, fileName), json); }
            catch { /* read-only install dir: not fatal */ }
        }

        private static nint ReadGlobal(JToken? client, string name)
        {
            try
            {
                JToken? token = client?[name];
                if (token == null)
                {
                    SkinChangerLogic.Log($"[Offsets] MISSING client.dll::{name} in offsets.json");
                    return 0;
                }
                return (nint)token.Value<long>();
            }
            catch
            {
                SkinChangerLogic.Log($"[Offsets] MISSING client.dll::{name} in offsets.json");
                return 0;
            }
        }

        private static nint ParseOffset(JToken? classes, string cls, string field)
        {
            try
            {
                JToken? token = classes?[cls]?["fields"]?[field];
                if (token == null)
                {
                    SkinChangerLogic.Log($"[Offsets] MISSING {cls}::{field} in client_dll.json");
                    return 0;
                }
                return (nint)token.Value<long>();
            }
            catch
            {
                SkinChangerLogic.Log($"[Offsets] MISSING {cls}::{field} in client_dll.json");
                return 0;
            }
        }

        private static bool Validate()
        {
            (string Name, nint Value)[] required =
            {
                (nameof(dwEntityList), dwEntityList),
                (nameof(dwLocalPlayerController), dwLocalPlayerController),
                (nameof(m_hPawn), m_hPawn),
                (nameof(m_pWeaponServices), m_pWeaponServices),
                (nameof(m_hMyWeapons), m_hMyWeapons),
                (nameof(m_AttributeManager), m_AttributeManager),
                (nameof(m_Item), m_Item),
                (nameof(m_AttributeList), m_AttributeList),
                (nameof(m_Attributes), m_Attributes),
                (nameof(m_iItemDefinitionIndex), m_iItemDefinitionIndex),
                (nameof(m_iItemIDHigh), m_iItemIDHigh),
                (nameof(m_nFallbackPaintKit), m_nFallbackPaintKit),
                (nameof(m_nFallbackSeed), m_nFallbackSeed),
                (nameof(m_flFallbackWear), m_flFallbackWear),
                (nameof(m_nFallbackStatTrak), m_nFallbackStatTrak),
            };

            bool ok = true;
            foreach ((string name, nint value) in required)
            {
                if (value == 0)
                {
                    SkinChangerLogic.Log($"[Offsets] REQUIRED offset '{name}' is 0 - skin changer disabled.");
                    ok = false;
                }
            }
            return ok;
        }

        private static void DumpToLog()
        {
            SkinChangerLogic.Log($"[Offsets] source={Source} loaded={Loaded}");
            SkinChangerLogic.Log($"[Offsets] dwEntityList=0x{dwEntityList:X} dwLocalPlayerController=0x{dwLocalPlayerController:X} dwLocalPlayerPawn=0x{dwLocalPlayerPawn:X}");
            SkinChangerLogic.Log($"[Offsets] m_hPawn=0x{m_hPawn:X} m_pWeaponServices=0x{m_pWeaponServices:X} m_hMyWeapons=0x{m_hMyWeapons:X}");
            SkinChangerLogic.Log($"[Offsets] m_AttributeManager=0x{m_AttributeManager:X} m_Item=0x{m_Item:X} m_AttributeList=0x{m_AttributeList:X} m_Attributes=0x{m_Attributes:X}");
            SkinChangerLogic.Log($"[Offsets] -> item view at entity+0x{m_AttributeManager + m_Item:X}, attribute count at entity+0x{EntityAttributeCountOffset:X}");
            SkinChangerLogic.Log($"[Offsets] m_iItemDefinitionIndex=0x{m_iItemDefinitionIndex:X} m_iItemIDHigh=0x{m_iItemIDHigh:X} m_iAccountID=0x{m_iAccountID:X} m_iEntityQuality=0x{m_iEntityQuality:X} m_bInitialized=0x{m_bInitialized:X}");
            SkinChangerLogic.Log($"[Offsets] m_nFallbackPaintKit=0x{m_nFallbackPaintKit:X} m_nFallbackSeed=0x{m_nFallbackSeed:X} m_flFallbackWear=0x{m_flFallbackWear:X} m_nFallbackStatTrak=0x{m_nFallbackStatTrak:X}");
            SkinChangerLogic.Log($"[Offsets] m_EconGloves=0x{m_EconGloves:X} m_bNeedToReApplyGloves=0x{m_bNeedToReApplyGloves:X} m_hViewmodelAttachment=0x{m_hViewmodelAttachment:X}");
            SkinChangerLogic.Log($"[Offsets] m_pGameSceneNode=0x{m_pGameSceneNode:X} m_modelState=0x{m_modelState:X} m_MeshGroupMask=0x{m_MeshGroupMask:X}");
        }
    }
}
