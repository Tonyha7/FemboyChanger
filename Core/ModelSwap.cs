using System;
using System.Collections.Generic;

namespace FemboyChanger.Core
{
    /// <summary>
    /// Reloads a weapon entity's model after we change its item definition.
    ///
    /// A weapon's model is a resource handle resolved once, when the entity is created. Writing a
    /// new <c>m_iItemDefinitionIndex</c> makes the item view describe (say) a Stiletto Knife and
    /// the paint kit resolves correctly, but the entity keeps rendering the model it spawned with -
    /// which is why picking a knife looked like nothing happened even though every field was right.
    ///
    /// The fix is to ask client.dll to do what it does at spawn:
    /// <code>
    ///   itemDef   = itemView->vtable[16]()   // C_EconItemView::GetStaticData
    ///   modelPath = itemDef->vtable[7]()     // the item's world model path
    ///   CBaseModelEntity::SetModel(weapon, modelPath)
    /// </code>
    /// Only SetModel has to be located by signature; the other two are virtual calls the stub
    /// resolves inside the process, so there is nothing else to keep up to date per build.
    /// </summary>
    public static class ModelSwap
    {
        /// <summary>
        /// <c>CBaseModelEntity::SetModel(const char*)</c> - looks the path up in the resource
        /// system and hands the handle to SetModelHandle. Unique in the current client.dll.
        /// </summary>
        private const string SetModelSig =
            "40 53 48 83 EC 20 48 8B D9 4C 8B C2 48 8B 0D ? ? ? ? 48 8D 54 24 40 48 8B 01 " +
            "FF 50 60 48 8B 54 24 40 48 8B CB E8 ? ? ? ? 48 83 C4 20 5B C3";

        private const int GetStaticDataVIndex = 16; // C_EconItemView
        private const int GetModelPathVIndex = 7;   // item definition

        private static nint _setModel = -1; // -1 = not looked up yet, 0 = not found

        public static void Reset() => _setModel = -1;

        public static bool Available(Memory mem) => Resolve(mem) != 0;

        private static nint Resolve(Memory mem)
        {
            if (_setModel == -1)
            {
                _setModel = mem.SigScan(SetModelSig);
                if (_setModel == 0)
                    SkinChangerLogic.Log("[Model] CBaseModelEntity::SetModel signature not found - knife models cannot be swapped.");
                else
                    SkinChangerLogic.Log($"[Model] SetModel at 0x{_setModel:X} (client.dll+0x{_setModel - mem.ClientDll:X})");
            }
            return _setModel;
        }

        /// <summary>Rebuilds <paramref name="weapon"/>'s model from its current item definition.</summary>
        public static bool Refresh(Memory mem, nint weapon, nint itemView)
        {
            nint setModel = Resolve(mem);
            if (setModel == 0) return false;

            byte[] stub = BuildStub(weapon, itemView, setModel);
            bool ok = mem.CallStub(stub);
            if (ok) SkinChangerLogic.Log($"[Model] Reloaded the model of weapon 0x{weapon:X}");
            return ok;
        }

        private static byte[] BuildStub(nint weapon, nint itemView, nint setModel)
        {
            var code = new List<byte>(80);

            code.AddRange(new byte[] { 0x48, 0x83, 0xEC, 0x28 });        // sub rsp, 28h
            MovRcx(code, itemView);                                      // mov rcx, itemView
            code.AddRange(new byte[] { 0x48, 0x8B, 0x01 });              // mov rax, [rcx]
            code.AddRange(new byte[] { 0xFF, 0x90 });                    // call qword ptr [rax+disp32]
            code.AddRange(BitConverter.GetBytes(GetStaticDataVIndex * 8));
            code.AddRange(new byte[] { 0x48, 0x85, 0xC0 });              // test rax, rax
            code.AddRange(new byte[] { 0x74, 0x27 });                    // jz done (39 bytes ahead)

            code.AddRange(new byte[] { 0x48, 0x8B, 0xC8 });              // mov rcx, rax
            code.AddRange(new byte[] { 0x48, 0x8B, 0x01 });              // mov rax, [rcx]
            code.AddRange(new byte[] { 0xFF, 0x50, GetModelPathVIndex * 8 }); // call qword ptr [rax+38h]
            code.AddRange(new byte[] { 0x48, 0x85, 0xC0 });              // test rax, rax
            code.AddRange(new byte[] { 0x74, 0x19 });                    // jz done (25 bytes ahead)

            code.AddRange(new byte[] { 0x48, 0x8B, 0xD0 });              // mov rdx, rax
            MovRcx(code, weapon);                                        // mov rcx, weapon
            code.AddRange(new byte[] { 0x48, 0xB8 });                    // mov rax, setModel
            code.AddRange(BitConverter.GetBytes((long)setModel));
            code.AddRange(new byte[] { 0xFF, 0xD0 });                    // call rax

            // done:
            code.AddRange(new byte[] { 0x48, 0x83, 0xC4, 0x28 });        // add rsp, 28h
            code.Add(0xC3);                                              // ret

            return code.ToArray();
        }

        private static void MovRcx(List<byte> code, nint value)
        {
            code.AddRange(new byte[] { 0x48, 0xB9 });
            code.AddRange(BitConverter.GetBytes((long)value));
        }
    }
}
