using System;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace FemboyChanger.Core
{
    public static class Offsets
    {
        public static nint dwEntityList;
        public static nint dwLocalPlayerPawn;
        public static nint dwLocalPlayerController;

        public static nint m_pWeaponServices;
        public static nint m_hMyWeapons;
        public static nint m_AttributeManager;
        public static nint m_Item;
        public static nint m_iItemDefinitionIndex;
        public static nint m_nFallbackPaintKit;
        public static nint m_nFallbackSeed;
        public static nint m_flFallbackWear;
        public static nint m_nFallbackStatTrak;
        public static nint m_iEntityQuality;
        public static nint m_iItemIDHigh;
        public static nint m_iAccountID;
        public static nint m_OriginalOwnerXuidLow;
        public static nint m_EconGloves;
        public static nint m_bNeedToReApplyGloves;
        public static nint m_bInitialized;
        public static nint m_pInventoryServices;
        public static nint m_unMusicID;
        public static nint m_AttributeList;
        public static nint m_Attributes;
        public static nint m_MeshGroupMask;
        public static nint m_pGameSceneNode;
        public static nint m_modelState;

        public static async Task LoadOffsetsAsync()
        {
            try
            {
                using var client = new HttpClient();
                
                string offsetsJson = await client.GetStringAsync("https://ob.tonyha7.com/offsets.json");
                var offsetsData = JObject.Parse(offsetsJson);
                
                dwEntityList = (nint)(offsetsData["client.dll"]["dwEntityList"] ?? 0);
                dwLocalPlayerController = (nint)(offsetsData["client.dll"]["dwLocalPlayerController"] ?? 0);
                dwLocalPlayerPawn = (nint)(offsetsData["client.dll"]["dwLocalPlayerPawn"] ?? 0);

                string clientDllJson = await client.GetStringAsync("https://ob.tonyha7.com/client_dll.json");
                var cdllData = JObject.Parse(clientDllJson);
                var classes = cdllData["client.dll"]["classes"];

                m_pInventoryServices = ParseOffset(classes, "CCSPlayerController", "m_pInventoryServices");
                m_unMusicID = ParseOffset(classes, "CCSPlayerController_InventoryServices", "m_unMusicID");

                m_pWeaponServices = ParseOffset(classes, "C_BasePlayerPawn", "m_pWeaponServices");
                
                m_hMyWeapons = ParseOffset(classes, "CPlayer_WeaponServices", "m_hMyWeapons");
                m_EconGloves = ParseOffset(classes, "C_CSPlayerPawn", "m_EconGloves");
                m_bNeedToReApplyGloves = ParseOffset(classes, "C_CSPlayerPawn", "m_bNeedToReApplyGloves");
                m_bInitialized = ParseOffset(classes, "C_EconItemView", "m_bInitialized");
                
                m_iEntityQuality = ParseOffset(classes, "C_EconItemView", "m_iEntityQuality");

                m_nFallbackPaintKit = ParseOffset(classes, "C_EconEntity", "m_nFallbackPaintKit");
                m_nFallbackStatTrak = ParseOffset(classes, "C_EconEntity", "m_nFallbackStatTrak");
                m_flFallbackWear = ParseOffset(classes, "C_EconEntity", "m_flFallbackWear");
                m_nFallbackSeed = ParseOffset(classes, "C_EconEntity", "m_nFallbackSeed");
                m_OriginalOwnerXuidLow = ParseOffset(classes, "C_EconEntity", "m_OriginalOwnerXuidLow");

                m_AttributeManager = ParseOffset(classes, "C_EconEntity", "m_AttributeManager");
                m_Item = ParseOffset(classes, "C_AttributeContainer", "m_Item");

                m_AttributeList = ParseOffset(classes, "C_EconItemView", "m_AttributeList");
                m_Attributes = ParseOffset(classes, "CAttributeList", "m_Attributes");
                m_iItemDefinitionIndex = ParseOffset(classes, "C_EconItemView", "m_iItemDefinitionIndex");
                m_iAccountID = ParseOffset(classes, "C_EconItemView", "m_iAccountID");
                m_iItemIDHigh = ParseOffset(classes, "C_EconItemView", "m_iItemIDHigh");

                m_pGameSceneNode = ParseOffset(classes, "C_BaseEntity", "m_pGameSceneNode");
                m_modelState = ParseOffset(classes, "CSkeletonInstance", "m_modelState");
                m_MeshGroupMask = ParseOffset(classes, "CModelState", "m_MeshGroupMask");
                
                SkinChangerLogic.Log($"Offsets loaded! dwEntityList: {dwEntityList:X}, dwLocalPlayerPawn: {dwLocalPlayerPawn:X}");
                SkinChangerLogic.Log($"m_iItemIDHigh: {m_iItemIDHigh:X}, m_nFallbackPaintKit: {m_nFallbackPaintKit:X}");
            }
            catch (Exception ex)
            {
                SkinChangerLogic.Log("Failed to load offsets: " + ex.Message);
                Console.WriteLine("Failed to load offsets: " + ex.Message);
            }
        }

        private static nint ParseOffset(JToken classes, string cls, string field)
        {
            try {
                return (nint)classes[cls]["fields"][field];
            } catch {
                return 0;
            }
        }
    }
}