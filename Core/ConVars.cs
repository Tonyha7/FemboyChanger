using System;

namespace FemboyChanger.Core
{
    /// <summary>
    /// Reads and writes a client convar's value directly.
    ///
    /// client.dll gets a convar's value with <c>*(ConVarRef + 8)</c> -&gt; ConVarData, then the
    /// value lives at ConVarData+88 (sub_1818622D0; the +48 flags word only adds a per-split-screen
    /// stride, which is 0 for slot 0). The ConVarRef object itself is located by finding the name
    /// string and the registration call that references it, so nothing is hardcoded to a build.
    /// </summary>
    public static class ConVars
    {
        private const int ConVarRefDataOffset = 8;
        private const int ConVarDataValueOffset = 88;

        /// <summary>
        /// <c>lea rdx, [rip+name]</c> ; <c>lea rcx, [rip+convarRef]</c> ; <c>call RegisterConVar</c>
        /// - the tail of every convar registration.
        /// </summary>
        private const string RegistrationPattern = "48 8D 15 ? ? ? ? 48 8D 0D ? ? ? ? E8";

        /// <summary>
        /// Gates C_EconEntity::UsesLegacyModel (sub_1807E72F0):
        ///   1     -&gt; every weapon uses its legacy model
        ///   0     -&gt; no weapon ever does, whatever the paint kit says
        ///   other -&gt; per paint kit, via use_legacy_model
        /// </summary>
        public const string WeaponSkinForceLegacy = "weapon_skin_force_legacy";

        /// <summary>Per paint kit - the value that lets a legacy skin pick its own model.</summary>
        public const int ForceLegacyPerPaintKit = 2;

        /// <summary>Address of the int value, or 0 if it could not be located.</summary>
        public static nint FindValueAddress(Memory mem, string name)
        {
            nint nameAddr = mem.FindAsciiString(name);
            if (nameAddr == 0)
            {
                SkinChangerLogic.Log($"[ConVar] Name string '{name}' not found in client.dll.");
                return 0;
            }

            foreach (nint site in mem.ScanAll(RegistrationPattern, 20000))
            {
                if (mem.ResolveRip(site, 3, 7) != nameAddr) continue;

                nint convarRef = mem.ResolveRip(site + 7, 3, 7);
                if (convarRef == 0) continue;

                nint data = mem.Read<nint>(convarRef + ConVarRefDataOffset);
                if (data == 0)
                {
                    SkinChangerLogic.Log($"[ConVar] '{name}' ref at 0x{convarRef:X} has no data pointer yet.");
                    return 0;
                }
                return data + ConVarDataValueOffset;
            }

            SkinChangerLogic.Log($"[ConVar] No registration site found for '{name}'.");
            return 0;
        }
    }
}
